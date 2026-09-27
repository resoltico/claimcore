module internal ClaimCore.IntegrationTests.ManagedCopyPhysicalOwnerSetup

open System
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
open ClaimCore.IntegrationTests.DatabaseVerifyDataTests
open ClaimCore.IntegrationTests.ManagedCopyAttestationFixture
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture
open ClaimCore.IntegrationTests.ManagedCopyIngestTests
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.ManagedCopyPhysicalProcessDocuments
open ClaimCore.IntegrationTests.ManagedCopyPhysicalProofFixture
open ClaimCore.IntegrationTests.RestorePhysicalCopyFixture
open ClaimCore.IntegrationTests.RestorePhysicalArchiveEvidence
open ClaimCore.IntegrationTests.RestorePhysicalProcess
open ClaimCore.TestSupport

let registeredVerifier
    (runtime: Runtime)
    owner
    holder
    (connection: NpgsqlConnection)
    (witness: WitnessProtocol)
    (capture: PhysicalCopyCapture)
    =
    let keyId, eventId = Guid.NewGuid(), Guid.NewGuid()
    let digest = SHA256.HashData(capture.SignerPublicKey)

    let ownerApproval, holderApproval =
        approvePair
            runtime
            owner
            holder
            keyId
            digest
            CopySignerAction.Register
            CopySignerPurpose.RestoreCopyVerifier

    ManagedCopySignerAdministration.register
        connection
        witness
        eventId
        keyId
        CopySignerPurpose.RestoreCopyVerifier
        capture.SignerPublicKey
        ownerApproval
        holderApproval
    |> await
    |> appliedSigner eventId

    keyId

[<NoEquality; NoComparison>]
type VerifiedPhysicalCopy =
    {
        CopyId: Guid
        VerificationEventId: Guid
        ArchiveObjectId: Guid
        ProofPath: string
        SignaturePath: string
        CiphertextPath: string
        Cluster: string
        Kind: string
        WitnessCutoff: int64
    }

let registerCopy
    (owner: string)
    (copy: PhysicalCopyDescriptor)
    (captureTip: Snapshot)
    (witness: WitnessProtocol)
    (connection: NpgsqlConnection)
    (copyKeyId: Guid)
    (copyKey: NSec.Cryptography.Key)
    (algorithm: NSec.Cryptography.SignatureAlgorithm)
    (commitment: byte array)
    =
    let copyId, copyEventId = Guid.NewGuid(), Guid.NewGuid()

    let canonical =
        registration owner copy captureTip copyKeyId copyEventId copyId commitment

    let signature = algorithm.Sign(copyKey, canonical)

    ManagedCopyAdministration.ingest connection witness canonical signature
    |> await
    |> acceptedCopy copyEventId

    let original =
        ManagedCopyRegistrationAttestation.parse canonical
        |> Option.defaultWith (fun () -> failtest "Registered physical copy is invalid.")

    original, canonical

let private proofPaths (capture: PhysicalCopyCapture) (original: ManagedCopyAttestation) =
    let root =
        Path.Combine(capture.ScratchRoot, "registered-proof", original.CopyId.ToString("N"))

    Directory.CreateDirectory(
        root,
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
    )
    |> ignore

    root, Path.Combine(root, "physical-proof.json"), Path.Combine(root, "physical-proof.sig")

let private assertInstallationBinding (capture: PhysicalCopyCapture) (inputDocument: byte array) =
    use inputJson = System.Text.Json.JsonDocument.Parse(inputDocument)

    Expect.equal
        (inputJson.RootElement.GetProperty("installationId").GetString())
        (capture.Facts.InstallationId.ToString("D"))
        "Physical verifier input binds captured installation"

let physicalProof
    (capture: PhysicalCopyCapture)
    (copy: PhysicalCopyDescriptor)
    (original: ManagedCopyAttestation)
    (tip: Snapshot)
    verifierKeyId
    verifierHolder
    eventId
    commitment
    =
    Expect.equal
        tip.Identity.InstallationId
        capture.Facts.InstallationId
        "Physical verifier source installation must match captured pair"

    Expect.notEqual
        capture.WitnessDatabaseName
        capture.DatabaseName
        "Physical witness and primary database targets are separate"

    let privateRoot, proofPath, signaturePath = proofPaths capture original

    let inputDocument =
        verifierOverride
            capture
            copy
            original
            tip
            verifierKeyId
            verifierHolder
            eventId
            proofPath
            signaturePath
            commitment

    assertInstallationBinding capture inputDocument

    let overridePath = privateBytes privateRoot "verifier-input.json" inputDocument

    let script =
        Path.Combine(RepositoryRoot.find (), "eng/backup/Test-VerifyManagedCopy.py")

    let code, result, stage = run "python3" [ script; "--registered"; overridePath ]

    Expect.equal
        code
        0
        ("Fixed " + copy.Cluster + " " + copy.Kind + " verifier refused at " + stage)

    Expect.equal
        result
        "managed-copy-registered-proof=created"
        "Only bounded test-only status is emitted."

    let proof = File.ReadAllBytes(proofPath)
    let signed = File.ReadAllBytes(signaturePath)

    Expect.isSome
        (ManagedCopyPhysicalProofCodec.parse proof)
        "Actual physical copy proof is strict canonical metadata."

    proofPath, signaturePath, proof, signed

let processFiles directory owner writer witness physicalConfig =
    let capability =
        Environment.GetEnvironmentVariable("CLAIMCORE_WRITER_CAPABILITY_FILE")
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Synthetic writer capability file is absent.")

    files directory owner writer witness
    @ [
        "CLAIMCORE_COPY_PHYSICAL_INPUT_FILE", physicalConfig
        "CLAIMCORE_WRITER_CAPABILITY_FILE", capability
    ]

let invoke files attestation signature =
    let code, response =
        runCommand "verify-managed-copy" [ attestation; signature ] files

    use response = response
    code, response.RootElement.Clone()
