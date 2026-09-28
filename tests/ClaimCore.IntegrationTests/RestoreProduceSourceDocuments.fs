module internal ClaimCore.IntegrationTests.RestoreProduceSourceDocuments

open System
open System.IO
open System.Security.Cryptography
open System.Text
open NSec.Cryptography
open ClaimCore.Database
open ClaimCore.IntegrationTests.ManagedCopyPhysicalOwnerSetup
open ClaimCore.IntegrationTests.RestorePhysicalArchiveEvidence
open ClaimCore.IntegrationTests.RestoreProduceSourceEvidence

[<NoEquality; NoComparison>]
type private SignedSources =
    {
        Cycle: Guid
        CaptureSequence: int64
        CaptureHash: string
        CheckpointRoot: string
        InventoryRoot: string
        ManifestFile: string
        ManifestSignatureFile: string
        ManifestSha: string
        CheckpointFile: string
        CheckpointSignatureFile: string
        InventoryFile: string
        InventorySignatureFile: string
        BarrierFile: string
        BarrierSignatureFile: string
        PrimaryBase: PhysicalArchiveCopy
        WitnessBase: PhysicalArchiveCopy
    }

let private objectFor
    (capture: PhysicalCopyCapture)
    (fresh: RegisteredWalCapture)
    (verified: VerifiedPhysicalCopy)
    =
    let descriptor =
        (capture.Objects |> List.filter (fun item -> item.Kind = "BASE"))
        @ fresh.Objects
        |> List.find (fun item -> item.CiphertextPath = verified.CiphertextPath)

    {
        ObjectId = verified.ArchiveObjectId
        CopyId = verified.CopyId
        Cluster = descriptor.Cluster
        Kind = descriptor.Kind
        RelativePath = descriptor.RelativePath
        Sha256 = descriptor.CiphertextSha256
        Bytes = descriptor.CiphertextBytes
        WalSegment = descriptor.WalSegment
        WalSegmentBytes =
            if descriptor.Kind = "WAL" then
                Some descriptor.WalSegmentBytes
            else
                None
    }

let private binarySha () =
    use stream = File.OpenRead(typeof<DatabaseCommand>.Assembly.Location)
    SHA256.HashData(stream) |> Convert.ToHexStringLower

let private baseObject cluster (capture: PhysicalCopyCapture) =
    capture.Objects
    |> List.find (fun item -> item.Cluster = cluster && item.Kind = "BASE")

let private writeBarrier
    facts
    cycle
    captureSequence
    captureHash
    (primaryBase: PhysicalArchiveCopy)
    (witnessBase: PhysicalArchiveCopy)
    (fresh: RegisteredWalCapture)
    sourceRoot
    (reportKey: Key)
    =
    barrier
        facts
        cycle
        captureSequence
        captureHash
        primaryBase.WalEndLsn.Value
        witnessBase.WalEndLsn.Value
        fresh.PrimaryHorizon
        fresh.WitnessHorizon
    |> fun bytes -> writeSigned sourceRoot "barrier" bytes reportKey

let private sourceRecord
    cycle
    captureSequence
    captureHash
    checkpointRoot
    inventoryRoot
    (manifestFile, manifestSignatureFile, manifestSha)
    (checkpointFile, checkpointSignatureFile)
    (inventoryFile, inventorySignatureFile)
    (barrierFile, barrierSignatureFile)
    primaryBase
    witnessBase
    =
    {
        Cycle = cycle
        CaptureSequence = captureSequence
        CaptureHash = captureHash
        CheckpointRoot = checkpointRoot
        InventoryRoot = inventoryRoot
        ManifestFile = manifestFile
        ManifestSignatureFile = manifestSignatureFile
        ManifestSha = manifestSha
        CheckpointFile = checkpointFile
        CheckpointSignatureFile = checkpointSignatureFile
        InventoryFile = inventoryFile
        InventorySignatureFile = inventorySignatureFile
        BarrierFile = barrierFile
        BarrierSignatureFile = barrierSignatureFile
        PrimaryBase = primaryBase
        WitnessBase = witnessBase
    }

let private signedSources
    (capture: PhysicalCopyCapture)
    (fresh: RegisteredWalCapture)
    (facts: RestoredPairFacts)
    (primary: VerifiedPhysicalCopy)
    (witness: VerifiedPhysicalCopy)
    (reportKey: Key)
    (checkpointKey: Key)
    =
    let cycle = Guid.NewGuid()
    let sourceRoot = directory capture.ScratchRoot "restore-signed-source"
    let checkpointRoot = directory capture.ScratchRoot "restore-checkpoints"
    let inventoryRoot = directory capture.ScratchRoot "restore-inventory"
    let capturedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
    let primaryBase = baseObject "PRIMARY" capture
    let witnessBase = baseObject "WITNESS" capture
    let captureSequence = capture.CaptureTip.TipSequence
    let captureHash = Convert.ToHexStringLower(capture.CaptureTip.TipHash)

    let manifestFile, manifestSignatureFile, manifestSha =
        manifest facts cycle capture.CaptureTip primary.CopyId witness.CopyId
        |> fun bytes -> writeSigned sourceRoot "manifest" bytes reportKey

    let checkpointFile, checkpointSignatureFile, _ =
        checkpoint facts cycle capturedAt
        |> fun bytes -> writeSigned checkpointRoot "checkpoint" bytes checkpointKey

    let inventoryFile, inventorySignatureFile, _ =
        inventory facts cycle
        |> fun bytes -> writeSigned inventoryRoot "inventory" bytes checkpointKey

    let barrierFile, barrierSignatureFile, _ =
        writeBarrier
            facts
            cycle
            captureSequence
            captureHash
            primaryBase
            witnessBase
            fresh
            sourceRoot
            reportKey

    sourceRecord
        cycle
        captureSequence
        captureHash
        checkpointRoot
        inventoryRoot
        (manifestFile, manifestSignatureFile, manifestSha)
        (checkpointFile, checkpointSignatureFile)
        (inventoryFile, inventorySignatureFile)
        (barrierFile, barrierSignatureFile)
        primaryBase
        witnessBase

let private publication (facts: RestoredPairFacts) reportKeyId checkpointKeyId publicationRoot =
    {
        ManifestSha256 = publicationRoot
        VerifierBinarySha256 = binarySha ()
        ReportSignerKeyId = reportKeyId
        CheckpointSignerKeyId = checkpointKeyId
        InstallationId = facts.InstallationId
        LineageId = facts.LineageId
        Epoch = facts.Epoch
        WriterGeneration = facts.WriterGeneration
        WitnessCutoff = facts.WitnessCutoff
        WitnessCutoffHash = facts.WitnessCutoffHash
    }
    : TrustedRestorePublication

let private evidenceIndex
    (capture: PhysicalCopyCapture)
    (fresh: RegisteredWalCapture)
    (facts: RestoredPairFacts)
    (verified: VerifiedPhysicalCopy list)
    (source: SignedSources)
    checkpointKeyId
    publicationRoot
    =
    {
        InstallationId = facts.InstallationId
        LineageId = facts.LineageId
        Epoch = facts.Epoch
        CycleId = source.Cycle
        BackupCaptureSequence = source.CaptureSequence
        BackupCaptureHash = source.CaptureHash
        PublicationManifestSha256 = publicationRoot
        ArchiveRoot = capture.ArchiveRoot
        ArchiveSetId = Guid.NewGuid()
        ArchiveObjects =
            verified |> List.map (objectFor capture fresh) |> List.sortBy _.RelativePath
        PrimaryCaptureWalEndpoint = source.PrimaryBase.WalEndLsn.Value
        WitnessCaptureWalEndpoint = source.WitnessBase.WalEndLsn.Value
        PrimaryRegisteredWalHorizon = fresh.PrimaryHorizon
        WitnessRegisteredWalHorizon = fresh.WitnessHorizon
        PrimaryWalEndpoint = facts.PrimaryWalEndpoint
        WitnessWalEndpoint = facts.WitnessWalEndpoint
        CheckpointRoot = source.CheckpointRoot
        CheckpointFile = source.CheckpointFile
        CheckpointSignatureFile = source.CheckpointSignatureFile
        ManifestFile = source.ManifestFile
        ManifestSignatureFile = source.ManifestSignatureFile
        ManifestSha256 = source.ManifestSha
        BarrierFile = source.BarrierFile
        BarrierSignatureFile = source.BarrierSignatureFile
        InventoryRoot = source.InventoryRoot
        InventorySnapshotFile = source.InventoryFile
        InventorySnapshotSignatureFile = source.InventorySignatureFile
        CheckpointSignerKeyId = checkpointKeyId
        CheckpointObjectId = Guid.NewGuid()
    }
    : RestoreEvidenceIndex

let build
    (capture: PhysicalCopyCapture)
    (fresh: RegisteredWalCapture)
    (facts: RestoredPairFacts)
    (verified: VerifiedPhysicalCopy list)
    reportKeyId
    checkpointKeyId
    (reportKey: Key)
    (checkpointKey: Key)
    =
    let bases = verified |> List.filter (fun item -> item.Kind = "BASE")
    let primary = bases |> List.find (fun item -> item.Cluster = "PRIMARY")
    let witness = bases |> List.find (fun item -> item.Cluster = "WITNESS")

    let source =
        signedSources capture fresh facts primary witness reportKey checkpointKey

    let publicationRoot =
        Encoding.ASCII.GetBytes("synthetic-only-reviewed-publication-root") |> sha

    let input: RestoreProduceInput =
        {
            Publication = publication facts reportKeyId checkpointKeyId publicationRoot
            Index =
                evidenceIndex capture fresh facts verified source checkpointKeyId publicationRoot
            ReportSigner = fun bytes -> SignatureAlgorithm.Ed25519.Sign(reportKey, bytes)
            CustodyKeyId = checkpointKeyId
            BackupCaptureSequence = source.CaptureSequence
            BackupCaptureHash = source.CaptureHash
            ValidUntil =
                DateTimeOffset(
                    DateTime.UtcNow.AddMinutes(30.).Ticks / TimeSpan.TicksPerSecond
                    * TimeSpan.TicksPerSecond,
                    TimeSpan.Zero
                )
        }

    input
