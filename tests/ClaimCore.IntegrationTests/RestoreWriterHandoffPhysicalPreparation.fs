module internal ClaimCore.IntegrationTests.RestoreWriterHandoffPhysicalPreparation

open System
open System.IO
open System.Security.Cryptography
open System.Text
open Expecto
open ClaimCore.Database
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.RestorePhysicalArchiveEvidence
open ClaimCore.IntegrationTests.RestorePhysicalConnections
open ClaimCore.IntegrationTests.RestorePhysicalWalRetention
open ClaimCore.IntegrationTests.RestoreWriterHandoffFencePins
open ClaimCore.IntegrationTests.RestoreWriterHandoffOwnerApprovals
open ClaimCore.IntegrationTests.RestoreWriterHandoffPrivateFixture

[<NoEquality; NoComparison>]
type PreparedPhysicalW1 =
    {
        Root: string
        ReportPath: string
        ReportSignaturePath: string
        IndexPath: string
        NewCapability: byte array
        NewCapabilityPath: string
        Witness: WitnessProtocol
        CheckedAt: DateTimeOffset
        ValidUntil: DateTimeOffset
        Fence: byte array
        FenceSha: string
        FencePath: string
        FenceSignaturePath: string
        HandoffId: Guid
        Reviewed: Snapshot
        Approved: Snapshot
        FirstApproval: Guid
        SecondApproval: Guid
        SignedInventoryFileSha256: string
    }

let private prospectiveFence
    (report: RestoreReportClaims)
    (facts: RestoredPairFacts)
    (access: RestoredPairAccess)
    (input: RestoreProduceInput)
    (produced: SignedRestoreProduction)
    checkedAt
    validUntil
    =
    let probeSha =
        SHA256.HashData(Encoding.ASCII.GetBytes("synthetic-old-runtime-disposed"))
        |> Convert.ToHexStringLower

    fenceBody
        report
        facts.WriterGeneration
        (facts.WriterGeneration + 1L)
        input.Index.CheckpointSignerKeyId
        probeSha
        (observed access)
        produced.Evidence.ReportSha256
        checkedAt
        validUntil

let private approvedContext
    root
    (reportPath, reportSignaturePath, indexPath)
    (newCapability: byte array)
    newCapabilityPath
    (witness: WitnessProtocol)
    (report: RestoreReportClaims)
    facts
    access
    (input: RestoreProduceInput)
    (produced: SignedRestoreProduction)
    =
    let checkedAt =
        DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 12L)

    let validUntil = checkedAt.AddMinutes(9.)
    let fence = prospectiveFence report facts access input produced checkedAt validUntil
    let fenceSha = fenceDigest fence
    let inventorySha = report.SignedInventoryFileSha256

    let handoffId, reviewed, approved, firstApproval, secondApproval =
        approveOwners
            access
            witness
            inventorySha
            fenceSha
            input.Index.CheckpointSignerKeyId
            (SHA256.HashData(newCapability))
            validUntil

    {
        Root = root
        ReportPath = reportPath
        ReportSignaturePath = reportSignaturePath
        IndexPath = indexPath
        NewCapability = newCapability
        NewCapabilityPath = newCapabilityPath
        Witness = witness
        CheckedAt = checkedAt
        ValidUntil = validUntil
        Fence = fence
        FenceSha = fenceSha
        FencePath = Path.Combine(root, "old-writer-fence.json")
        FenceSignaturePath = Path.Combine(root, "old-writer-fence.sig")
        HandoffId = handoffId
        Reviewed = reviewed
        Approved = approved
        FirstApproval = firstApproval
        SecondApproval = secondApproval
        SignedInventoryFileSha256 = inventorySha
    }

let withPrepared
    (capture: PhysicalCopyCapture)
    (registered: RegisteredWalCapture)
    (facts: RestoredPairFacts)
    (access: RestoredPairAccess)
    containers
    (input: RestoreProduceInput)
    (produced: SignedRestoreProduction)
    keyId
    action
    =
    let report =
        DatabaseRestoreReportClaims.parse produced.Evidence.Report DateTimeOffset.UtcNow
        |> Option.defaultWith (fun () -> failtest "Signed pre-W1 report is invalid")

    requireRegisteredHorizonSegments capture registered containers
    let root = createRoot capture.ScratchRoot
    let paths = reportFiles root produced
    let newCapability = RandomNumberGenerator.GetBytes(32)

    try
        let newCapabilityPath = rawCapability root newCapability
        use witness = restoredWitness access capture.CaptureTip.Identity keyId
        witness.AdmitReadOnly()

        approvedContext
            root
            paths
            newCapability
            newCapabilityPath
            witness
            report
            facts
            access
            input
            produced
        |> action
    finally
        CryptographicOperations.ZeroMemory(newCapability)
