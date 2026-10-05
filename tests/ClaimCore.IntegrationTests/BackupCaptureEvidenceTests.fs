module ClaimCore.IntegrationTests.BackupCaptureEvidenceTests

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Database
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopyIngestTests
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.BackupCaptureEvidenceFixture
open ClaimCore.IntegrationTests.BackupCaptureEvidenceProcess
open ClaimCore.IntegrationTests.FixtureEnvironment

let private readback
    (evidence: IBackupCaptureEvidence)
    (held: BackupCaptureHeld)
    (files: BackupCaptureFiles)
    (cutoff: int64)
    (historical: unit -> BackupCaptureReceipt)
    (ownerReadback: unit -> int * string)
    =
    let sealedReceipt =
        evidence.Seal(held, files, CancellationToken.None)
        |> await
        |> Option.defaultWith (fun () -> failtest "Owner did not seal signed files.")

    Expect.equal sealedReceipt.WitnessSequence cutoff "Receipt binds the held tip."

    Expect.isTrue
        (evidence.Observe(sealedReceipt, CancellationToken.None) |> await)
        "Owner rereads exact private files."

    let reconciled = historical ()

    Expect.equal
        reconciled.ReceiptSha256
        sealedReceipt.ReceiptSha256
        "Restart readback preserves exact captured bytes."

    let code, status = ownerReadback ()

    Expect.equal
        (code, status)
        (0, "COMPLETED")
        "Owner CLI confirms only exact captured-byte readback."

    File.WriteAllBytes(files.PrimaryCiphertextPath, [| 9uy; 8uy; 7uy |])

    Expect.isFalse
        (evidence.Observe(sealedReceipt, CancellationToken.None) |> await)
        "Changed ciphertext invalidates readback."

    Expect.throws (fun () -> historical () |> ignore) "Changed bytes refuse historical readback."
    let code, status = ownerReadback ()

    Expect.equal
        (code, status)
        (4, "COMPLETION_UNKNOWN")
        "Changed bytes cannot settle an uncertain capture."


let private signerKeys
    (runtime: Runtime)
    principal
    copyHolder
    checkpointHolder
    (witness: WitnessProtocol)
    (ownerConnection: NpgsqlConnection)
    =
    let copyKey, _, copyKeyId, _ =
        registeredSigner
            runtime
            principal
            copyHolder
            CopySignerPurpose.CopyAttestor
            witness
            ownerConnection

    let checkpointKey, _, checkpointKeyId, _ =
        registeredSigner
            runtime
            principal
            checkpointHolder
            CopySignerPurpose.Checkpoint
            witness
            ownerConnection

    copyKey, copyKeyId, checkpointKey, checkpointKeyId

let private openAuditor (writer: string) (witness: WitnessProtocol) =
    let material = witnessKey ()
    let keyId = witness.KeyCustody.ActiveKeyId
    let custody = new KeyRing(keyId, [ keyId, material ]) :> IKeyCustody
    CryptographicOperations.ZeroMemory(material)

    try
        let protocol =
            new WitnessProtocol(
                Store.OpenAudit(
                    witnessRoleConnection (witnessAuditConnection ()) writer,
                    witness.Identity
                ),
                custody,
                witness.Identity
            )

        (protocol.AdmitReadOnly(CancellationToken.None).GetAwaiter().GetResult())
        custody, protocol
    with error ->
        custody.Dispose()
        raise error

let private capturedCutoff (witness: WitnessProtocol) =
    let tip = (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult())

    {
        InstallationId = witness.Identity.InstallationId
        LineageId = witness.Identity.LineageId
        Epoch = witness.Identity.Epoch
        WriterGeneration = tip.WriterGeneration
        WitnessSequence = tip.TipSequence
        WitnessHash = tip.TipHash
        AuthorityEvents = 0L
        AcceptedOperations = 0L
    }

let private heldLease (evidence: IBackupCaptureEvidence) cutoff =
    let now = DateTimeOffset.UtcNow

    evidence.Describe(cutoff, String.replicate 64 "a", Guid.NewGuid(), now, now.AddMinutes(5.))
    |> Option.defaultWith (fun () -> failtest "Owner lease was not described.")

let private performCapture
    parent
    archive
    checkpoint
    owner
    app
    writer
    (witness: WitnessProtocol)
    (ownerConnection: NpgsqlConnection)
    (witnessOwner: NpgsqlConnection)
    (auditedWitness: WitnessProtocol)
    copyKeyId
    copyKey
    checkpointKeyId
    checkpointKey
    =
    let cutoff = capturedCutoff witness

    let evidence =
        new DatabaseBackupCaptureEvidence(ownerConnection, witnessOwner, archive, checkpoint)
        :> IBackupCaptureEvidence

    let held = heldLease evidence cutoff

    let files =
        writeCapture held checkpoint copyKeyId copyKey checkpointKeyId checkpointKey

    let configured = processInputs parent owner app writer witness archive checkpoint

    readback
        evidence
        held
        files
        cutoff.WitnessSequence
        (fun () ->
            DatabaseBackupCaptureReconciliation.inspect
                ownerConnection
                auditedWitness
                archive
                checkpoint
                held.LeaseId
            |> await)
        (fun () -> processStatus configured held.LeaseId)

let private run owner app writer (witness: WitnessProtocol) =
    let principal = human "backup-capture-owner"
    let copyHolder = human "backup-capture-copy-holder"
    let checkpointHolder = human "backup-capture-checkpoint-holder"
    provision owner witness principal |> applied
    use runtime = openRuntime app writer
    grantCustodian runtime principal copyHolder
    grantCustodian runtime principal checkpointHolder
    use ownerConnection = new NpgsqlConnection(owner)
    ownerConnection.Open()

    let copyKey, copyKeyId, checkpointKey, checkpointKeyId =
        signerKeys runtime principal copyHolder checkpointHolder witness ownerConnection

    use copyKey = copyKey
    use checkpointKey = checkpointKey
    let borrowedCustody, auditedWitness = openAuditor writer witness
    use custody = borrowedCustody
    use auditedWitness = auditedWitness

    use witnessOwner =
        new NpgsqlConnection(witnessRoleConnection (witnessOwnerConnection ()) writer)

    witnessOwner.Open()
    let parent, archive, checkpoint = privateRoots ()

    try
        performCapture
            parent
            archive
            checkpoint
            owner
            app
            writer
            witness
            ownerConnection
            witnessOwner
            auditedWitness
            copyKeyId
            copyKey
            checkpointKeyId
            checkpointKey
    finally
        Directory.Delete(parent, true)

let private verifiedFiles =
    testCase
        "[CC-BACKUP-001] owner seals exact distinct signed capture files and detects changed bytes"
        (fun _ -> withAuthorityRuntimeDatabase run)

let tests = testList "owner backup capture evidence" [ verifiedFiles ]
