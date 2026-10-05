module ClaimCore.IntegrationTests.ManagedCopyPhysicalVerificationFixture

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
open ClaimCore.IntegrationTests.RestoreProducePhysicalChecks
open ClaimCore.TestSupport

open ClaimCore.IntegrationTests.ManagedCopyPhysicalOwnerSetup
open ClaimCore.IntegrationTests.ManagedCopyPhysicalOwnerRefusals

open ClaimCore.IntegrationTests.ManagedCopyPhysicalOwnerTransition
open ClaimCore.IntegrationTests.BackupHealthCommitInterleaving

[<NoEquality; NoComparison>]
type internal CopySession =
    {
        Owner: string
        App: string
        Writer: string
        Witness: WitnessProtocol
        Capture: PhysicalCopyCapture
        Connection: NpgsqlConnection
        CopyKeyId: Guid
        CopyKey: NSec.Cryptography.Key
        Algorithm: NSec.Cryptography.SignatureAlgorithm
        VerifierKeyId: Guid
        Commitment: byte array
        CommitmentKeyPath: string
        CaptureTip: Snapshot
    }

let private verifyOne (session: CopySession) afterVerify index copy =
    let original, registration =
        registerCopy
            session.Owner
            copy
            session.CaptureTip
            session.Witness
            session.Connection
            session.CopyKeyId
            session.CopyKey
            session.Algorithm
            session.Commitment

    let eventId = Guid.NewGuid()

    let proofPath, signaturePath, proof, _ =
        physicalProof
            session.Capture
            copy
            original
            ((session.Witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()))
            session.VerifierKeyId
            (holder session.Connection session.VerifierKeyId)
            eventId
            session.Commitment

    let verified =
        ManagedCopyPhysicalOwnerTransition.run
            {
                Owner = session.Owner
                App = session.App
                Writer = session.Writer
                Witness = session.Witness
                Capture = session.Capture
                Copy = copy
                Original = original
                Registration = registration
                CopyKey = session.CopyKey
                Algorithm = session.Algorithm
                EventId = eventId
                ProofPath = proofPath
                SignaturePath = signaturePath
                Proof = proof
                CommitmentKeyPath = session.CommitmentKeyPath
                CheckNegatives = index = 0
                ExpectedReceipts = int64 index + 1L
            }

    afterVerify session original registration proof verified
    verified

let private copySigner runtime principal copyHolder witness connection =
    registeredSigner runtime principal copyHolder CopySignerPurpose.CopyAttestor witness connection

let private verifyCopies select afterVerify afterAll (session: CopySession) =
    let copies = select session.Capture
    let verified = copies |> List.mapi (verifyOne session afterVerify)
    afterAll session verified
    verified

let internal verifySelected
    select
    afterVerify
    afterAll
    owner
    app
    writer
    (witness: WitnessProtocol)
    capture
    =
    let principal = human "physical-restore-owner"
    let copyHolder = human "real-base-copy-attestor"
    let verifierHolder = human "real-base-copy-verifier"
    use runtime = openRuntime app writer
    grantCustodian runtime principal copyHolder
    grantCustodian runtime principal verifierHolder
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    let copyKey, algorithm, copyKeyId, _ =
        copySigner runtime principal copyHolder witness connection

    use copyKey = copyKey

    let verifierKeyId =
        registeredVerifier runtime principal verifierHolder connection witness capture

    let commitment = RandomNumberGenerator.GetBytes(32)

    let commitmentKeyPath =
        privateBytes
            (Path.Combine(capture.ScratchRoot, "registered-proof"))
            "copy-location.key"
            commitment

    try
        let session =
            {
                Owner = owner
                App = app
                Writer = writer
                Witness = witness
                Capture = capture
                Connection = connection
                CopyKeyId = copyKeyId
                CopyKey = copyKey
                Algorithm = algorithm
                VerifierKeyId = verifierKeyId
                Commitment = commitment
                CommitmentKeyPath = commitmentKeyPath
                CaptureTip = capture.CaptureTip
            }

        verifyCopies select afterVerify afterAll session
    finally
        CryptographicOperations.ZeroMemory(commitment)
