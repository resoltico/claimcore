module internal ClaimCore.IntegrationTests.RestoreWriterHandoffPhysicalIsolation

open System
open System.Security.Cryptography
open Expecto
open NSec.Cryptography
open ClaimCore.Database
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.RestorePhysicalArchiveEvidence
open ClaimCore.IntegrationTests.RestorePhysicalConnections
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.RestoreWriterHandoffPhysicalPreparation
open ClaimCore.IntegrationTests.RestoreWriterHandoffPhysicalSettlement
open ClaimCore.IntegrationTests.RestoreWriterHandoffPrivateFixture
open ClaimCore.IntegrationTests.WriterHandoffDocuments

let private auditedIsolation (access: RestoredPairAccess) custody suppression approved =
    let _, tip, _ =
        DatabaseVerifyData.auditedRestoredWith
            access.Owner
            access.WitnessAudit
            custody
            suppression
            (fun _ _ _ _ stable -> stable.TipSequence)

    Expect.equal tip.TipSequence approved "Post-isolation full audit found no unreviewed authority"

let private signedPrepare
    (prepared: PreparedPhysicalW1)
    (input: RestoreProduceInput)
    (produced: SignedRestoreProduction)
    (checkpointKey: Key)
    =
    let canonical =
        WriterHandoffDocuments.prepare
            prepared.Witness.Identity
            prepared.HandoffId
            prepared.Approved.WriterGeneration
            prepared.Reviewed.TipSequence
            prepared.Reviewed.TipHash
            prepared.Approved.TipSequence
            prepared.Approved.TipHash
            (SHA256.HashData(prepared.NewCapability))
            input.Index.CheckpointSignerKeyId
            (Convert.FromHexString(prepared.FenceSha))
            (Convert.FromHexString(prepared.SignedInventoryFileSha256))
            (Convert.FromHexString(produced.Evidence.ReportSha256))
            prepared.FirstApproval
            prepared.SecondApproval
            prepared.ValidUntil

    let signature = SignatureAlgorithm.Ed25519.Sign(checkpointKey, canonical)
    let path, signaturePath = signedFile prepared.Root "prepare" canonical signature
    canonical, signature, path, signaturePath

let private settlementInput
    capture
    registered
    facts
    access
    containers
    input
    produced
    checkpointKey
    keyId
    (prepared: PreparedPhysicalW1)
    (custody: IKeyCustody)
    (suppression: SuppressionKeyFile)
    (canonical, signature, path, signaturePath)
    : PhysicalW1Settlement =
    {
        Capture = capture
        Registered = registered
        Facts = facts
        Access = access
        Containers = containers
        Input = input
        Produced = produced
        CheckpointKey = checkpointKey
        KeyId = keyId
        Witness = prepared.Witness
        Custody = custody
        Suppression = suppression
        Root = prepared.Root
        ReportPath = prepared.ReportPath
        ReportSignaturePath = prepared.ReportSignaturePath
        IndexPath = prepared.IndexPath
        FencePath = prepared.FencePath
        FenceSignaturePath = prepared.FenceSignaturePath
        NewCapabilityPath = prepared.NewCapabilityPath
        Fence = prepared.Fence
        Prepare = canonical
        PrepareSignature = signature
        PreparePath = path
        PrepareSignaturePath = signaturePath
        CheckedAt = prepared.CheckedAt
        ValidUntil = prepared.ValidUntil
        HandoffId = prepared.HandoffId
        NewCapability = prepared.NewCapability
    }

let withIsolated
    onSettled
    (capture: PhysicalCopyCapture)
    (registered: RegisteredWalCapture)
    facts
    (access: RestoredPairAccess)
    containers
    (input: RestoreProduceInput)
    (produced: SignedRestoreProduction)
    (checkpointKey: Key)
    keyId
    (prepared: PreparedPhysicalW1)
    =
    let renewedAccess = rotateWriterCredentials access
    let afterFenceKey = witnessKey ()
    use custody = new KeyRing(keyId, [ keyId, afterFenceKey ]) :> IKeyCustody
    CryptographicOperations.ZeroMemory(afterFenceKey)
    use suppression = SuppressionKeyFile.Load(suppressionKeyFile ())
    auditedIsolation renewedAccess custody suppression prepared.Approved.TipSequence

    match PrivateFileService.writeNew 32768 prepared.FencePath prepared.Fence with
    | Ok() -> ()
    | Error _ -> failtest "Prospective W1 fence body could not be retained"

    let canonical, signature, path, signaturePath =
        signedPrepare prepared input produced checkpointKey

    settlementInput
        capture
        registered
        facts
        renewedAccess
        containers
        input
        produced
        checkpointKey
        keyId
        prepared
        custody
        suppression
        (canonical, signature, path, signaturePath)
    |> settle onSettled
