module ClaimCore.IntegrationTests.ManagedCopyVerifiedRestoreTests

open System
open System.Data
open System.IO
open System.Security.Cryptography
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.ManagedCopyIngestTests
open ClaimCore.IntegrationTests.ManagedCopyAttestationFixture
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture
open ClaimCore.IntegrationTests.ManagedCopyPhysicalProofFixture

let private eventHash (connection: NpgsqlConnection) copyId =
    use command =
        new NpgsqlCommand(
            "SELECT event_hash FROM claimcore.managed_copies WHERE copy_id=@copy",
            connection
        )

    Sql.uuid command "copy" copyId

    match command.ExecuteScalar() with
    | :? (byte array) as value -> value
    | _ -> failtest "Synthetic copy event hash is unavailable."

let private register
    (owner: string)
    (connection: NpgsqlConnection)
    (witness: WitnessProtocol)
    (copyKeyId: Guid)
    (copyKey: NSec.Cryptography.Key)
    (algorithm: NSec.Cryptography.SignatureAlgorithm)
    =
    let copyId, eventId = Guid.NewGuid(), Guid.NewGuid()
    let bytes = registerBase owner (witness.Snapshot()) copyKeyId eventId copyId
    let signature = algorithm.Sign(copyKey, bytes)

    ManagedCopyAdministration.ingest connection witness bytes signature
    |> await
    |> acceptedCopy eventId

    copyId, bytes

let private signedTransition
    (connection: NpgsqlConnection)
    (witness: WitnessProtocol)
    (copyId: Guid)
    (registration: byte array)
    (copyKey: NSec.Cryptography.Key)
    (verifierKeyId: Guid)
    (verifierKey: NSec.Cryptography.Key)
    (algorithm: NSec.Cryptography.SignatureAlgorithm)
    =
    let original =
        ManagedCopyRegistrationAttestation.parse registration
        |> Option.defaultWith (fun () -> failtest "Synthetic BASE registration is invalid.")

    let actionTip = witness.Snapshot()
    let verifyEventId = Guid.NewGuid()

    let proof, proofSignature, checkedAt =
        signedBaseProof
            original
            actionTip
            verifyEventId
            2L
            verifierKeyId
            (holder connection verifierKeyId)
            verifierKey
            algorithm

    Expect.isSome
        (ManagedCopyPhysicalProofCodec.parse proof)
        "Strict proof parser admits the exact synthetic signed BASE metadata."

    let transition =
        transitionFromRegister
            registration
            verifyEventId
            2L
            "VERIFY"
            "RETAINED"
            (eventHash connection copyId)
            actionTip
        |> fun source ->
            changed
                source
                [
                    "verificationProofSha256", element (digest proof)
                    "lastVerifiedAt", element (stamp checkedAt)
                ]

    let signature = algorithm.Sign(copyKey, transition)
    verifyEventId, proof, proofSignature, transition, signature

let private assertWitnessedVerify
    (connection: NpgsqlConnection)
    (witness: WitnessProtocol)
    (verifyEventId: Guid)
    (proof: byte array)
    (proofSignature: byte array)
    (transition: byte array)
    (transitionSignature: byte array)
    =
    let before = witness.Snapshot().TipSequence

    match
        ManagedCopyVerifiedRestore.execute
            connection
            witness
            unavailableVerifier
            transition
            transitionSignature
        |> await
    with
    | AuthorityWriteOutcome.Refused -> ()
    | _ -> failtest "Missing physical proof did not refuse."

    Expect.equal
        (witness.Snapshot().TipSequence)
        before
        "A missing physical proof appends no witness event."

    let synthetic = syntheticVerifier proof proofSignature

    match
        ManagedCopyVerifiedRestore.execute
            connection
            witness
            synthetic
            transition
            transitionSignature
        |> await
    with
    | AuthorityWriteOutcome.Applied(id, revision) when id = verifyEventId && revision = 2L -> ()
    | _ -> failtest "Synthetic typed VERIFY did not co-commit."

let private auditAndTamper
    (app: string)
    (connection: NpgsqlConnection)
    (witness: WitnessProtocol)
    (verifyEventId: Guid)
    =
    use source = RuntimeDataSource.create app
    use audit = RuntimeDatabase.openConnection source
    let summary = DataAudit.run audit witness CancellationToken.None |> await

    Expect.equal
        summary.CopyPhysicalVerifications
        1L
        "Signed physical receipt is independently replayed."

    Expect.equal summary.OwnerManagedCopies 1L "Copy state remains audited."

    use tamper =
        new NpgsqlCommand(
            "UPDATE claimcore.managed_copy_verifications SET ed25519_signature="
            + "set_byte(ed25519_signature,0,get_byte(ed25519_signature,0)#1) "
            + "WHERE verification_event_id=@event",
            connection
        )

    Sql.uuid tamper "event" verifyEventId
    Expect.equal (tamper.ExecuteNonQuery()) 1 "One synthetic proof signature changed."

    Expect.throwsT<InvalidDataException>
        (fun () -> DataAudit.run audit witness CancellationToken.None |> await |> ignore)
        "Full audit quarantines altered retained physical proof."

let private verify owner app writer (witness: WitnessProtocol) =
    let principal = human "physical-copy-owner"
    let copyHolder = human "physical-copy-attestor"
    let verifierHolder = human "physical-copy-verifier"
    provision owner witness principal |> applied
    use runtime = openRuntime app writer
    grantCustodian runtime principal copyHolder
    grantCustodian runtime principal verifierHolder
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    let copyKey, algorithm, copyKeyId, _ =
        registeredSigner
            runtime
            principal
            copyHolder
            CopySignerPurpose.CopyAttestor
            witness
            connection

    use copyKey = copyKey

    let verifierKey, _, verifierKeyId, _ =
        registeredSigner
            runtime
            principal
            verifierHolder
            CopySignerPurpose.RestoreCopyVerifier
            witness
            connection

    use verifierKey = verifierKey

    let copyId, registration =
        register owner connection witness copyKeyId copyKey algorithm

    let verifyEventId, proof, proofSignature, transition, transitionSignature =
        signedTransition
            connection
            witness
            copyId
            registration
            copyKey
            verifierKeyId
            verifierKey
            algorithm

    assertWitnessedVerify
        connection
        witness
        verifyEventId
        proof
        proofSignature
        transition
        transitionSignature

    auditAndTamper app connection witness verifyEventId

let tests =
    testList
        "managed-copy physical verification"
        [
            testCase
                "[CC-BACKUP-001] synthetic typed physical proof drives witnessed VERIFY without claiming pair readiness"
                (fun _ -> withAuthorityRuntimeDatabase verify)
        ]
