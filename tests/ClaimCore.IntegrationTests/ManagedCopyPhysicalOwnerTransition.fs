module internal ClaimCore.IntegrationTests.ManagedCopyPhysicalOwnerTransition

open System
open System.IO
open System.Threading
open Expecto
open Npgsql
open NSec.Cryptography
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopyAttestationFixture
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture
open ClaimCore.IntegrationTests.ManagedCopyPhysicalProcessDocuments
open ClaimCore.IntegrationTests.ManagedCopyPhysicalOwnerSetup
open ClaimCore.IntegrationTests.ManagedCopyPhysicalOwnerRefusals
open ClaimCore.IntegrationTests.RestorePhysicalArchiveEvidence
open ClaimCore.TestSupport

[<NoEquality; NoComparison>]
type Request =
    {
        Owner: string
        App: string
        Writer: string
        Witness: WitnessProtocol
        Capture: PhysicalCopyCapture
        Copy: PhysicalCopyDescriptor
        Original: ManagedCopyAttestation
        Registration: byte array
        CopyKey: Key
        Algorithm: SignatureAlgorithm
        EventId: Guid
        ProofPath: string
        SignaturePath: string
        Proof: byte array
        CommitmentKeyPath: string
        CheckNegatives: bool
        ExpectedReceipts: int64
    }

let private transition (request: Request) =
    use connection = new NpgsqlConnection(request.Owner)
    connection.Open()

    use hash =
        new NpgsqlCommand(
            "SELECT event_hash FROM claimcore.managed_copies WHERE copy_id=@copy",
            connection
        )

    Sql.uuid hash "copy" request.Original.CopyId

    let prior =
        match hash.ExecuteScalar() with
        | :? (byte array) as value -> value
        | _ -> failtest "Registered copy event hash is absent."

    let checkedAt =
        ManagedCopyPhysicalProofCodec.parse request.Proof
        |> Option.map _.CheckedAt
        |> Option.defaultWith (fun () -> failtest "Physical report time is invalid.")

    transitionFromRegister
        request.Registration
        request.EventId
        2L
        "VERIFY"
        "RETAINED"
        prior
        (request.Witness.Snapshot())
    |> fun source ->
        changed
            source
            [
                "verificationProofSha256", element (digest request.Proof)
                "lastVerifiedAt", element (stamp checkedAt)
            ]

let private inputs (request: Request) transition =
    let root =
        Path.Combine(
            request.Capture.ScratchRoot,
            "registered-proof",
            request.Original.CopyId.ToString("N")
        )

    let attestation = privateBytes root "verify-transition.json" transition

    let signature =
        privateBytes
            root
            "verify-transition.sig"
            (request.Algorithm.Sign(request.CopyKey, transition))

    let config =
        ownerInput
            request.Original.CopyId
            request.Copy.CiphertextPath
            request.ProofPath
            request.SignaturePath
            request.CommitmentKeyPath
            (FileInfo(request.Copy.CiphertextPath).Length)

    let configPath = privateBytes root "owner-physical-input.json" config

    root,
    attestation,
    signature,
    processFiles root request.Owner request.Writer request.Witness configPath

let private audit (request: Request) before =
    Expect.equal
        (request.Witness.Snapshot().TipSequence)
        (before + 2L)
        "VERIFY adds one intent and one settlement."

    use source = RuntimeDataSource.create request.App
    use connection = RuntimeDatabase.openConnection source

    let summary =
        DataAudit.run connection request.Witness CancellationToken.None |> await

    Expect.equal
        summary.CopyPhysicalVerifications
        request.ExpectedReceipts
        "Real signed physical receipt is fully audited."

let private result (request: Request) =
    let parsed =
        ManagedCopyPhysicalProofCodec.parse request.Proof
        |> Option.defaultWith (fun () -> failtest "Physical receipt proof is invalid.")

    {
        CopyId = request.Original.CopyId
        VerificationEventId = request.EventId
        ArchiveObjectId = parsed.ArchiveObjectId
        ProofPath = request.ProofPath
        SignaturePath = request.SignaturePath
        CiphertextPath = request.Copy.CiphertextPath
        Cluster = request.Copy.Cluster
        Kind = request.Copy.Kind
        WitnessCutoff = parsed.WitnessCutoffSequence
    }

let run request =
    let canonical = transition request
    let root, attestation, signature, files = inputs request canonical
    let before = request.Witness.Snapshot().TipSequence

    if request.CheckNegatives then
        exercise
            request.Witness
            before
            root
            canonical
            request.Algorithm
            request.CopyKey
            request.Copy
            request.Original.CopyId
            request.ProofPath
            request.SignaturePath
            files
            attestation
            signature

    let code, response = invoke files attestation signature
    Expect.equal code 0 "Owner verify-managed-copy process accepts real signed physical proof."

    Expect.equal
        (response.GetProperty("operationOutcome").GetString())
        "COMPLETED"
        "One physical copy reached a definite retained state."

    audit request before
    result request
