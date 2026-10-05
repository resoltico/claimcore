module ClaimCore.IntegrationTests.RestoreReportPairBindingTests

open System
open Expecto
open ClaimCore.Database

let private digest = String('a', 64)
let private primaryId = Guid.NewGuid()
let private lineageId = Guid.NewGuid()
let private cycleId = Guid.NewGuid()
let private reportKey = Guid.NewGuid()
let private checkpointKey = Guid.NewGuid()

let private publication =
    {
        ManifestSha256 = digest
        VerifierBinarySha256 = digest
        ReportSignerKeyId = reportKey
        CheckpointSignerKeyId = checkpointKey
        InstallationId = primaryId
        LineageId = lineageId
        Epoch = 1L
        WriterGeneration = 1L
        WitnessCutoff = 8L
        WitnessCutoffHash = digest
    }

let private facts =
    {
        InstallationId = primaryId
        LineageId = lineageId
        Epoch = 1L
        WriterGeneration = 1L
        AuthorityRevision = 4L
        PrimarySystemId = "111111111111"
        PrimaryTimeline = 2L
        PrimaryWalEndpoint = "0/ABC"
        WitnessSystemId = "222222222222"
        WitnessTimeline = 3L
        WitnessWalEndpoint = "0/DEF"
        WitnessCutoff = 8L
        WitnessCutoffHash = digest
        PendingIntents = 0L
        ManagedCopyCount = 2L
        ManagedCopySnapshotSha256 = digest
        CatalogManifestSha256 = digest
    }

let private report =
    {
        Scope = "synthetic-only"
        InstallationId = facts.InstallationId
        LineageId = facts.LineageId
        Epoch = facts.Epoch
        CycleId = cycleId
        BackupCaptureSequence = 4L
        BackupCaptureHash = digest
        WitnessCutoff = facts.WitnessCutoff
        WitnessCutoffHash = facts.WitnessCutoffHash
        PrimarySystemId = facts.PrimarySystemId
        PrimaryTimeline = facts.PrimaryTimeline
        WitnessSystemId = facts.WitnessSystemId
        WitnessTimeline = facts.WitnessTimeline
        PrimaryRegisteredWalHorizon = "0/AAB"
        WitnessRegisteredWalHorizon = "0/DDE"
        SignerKeyId = reportKey
        VerifierBinarySha256 = digest
        EvidenceIndexSha256 = digest
        CheckpointSha256 = digest
        SignedInventoryFileSha256 = digest
        QuiescentBarrierSha256 = digest
        CatalogManifestSha256 = digest
        AuthorityRevision = facts.AuthorityRevision
        AuthorizedApprovers = []
        ArchiveCustody =
            {
                ObjectId = Guid.NewGuid()
                Sha256 = digest
                Bytes = 100L
            }
        CheckpointCustody =
            {
                ObjectId = Guid.NewGuid()
                Sha256 = digest
                Bytes = 100L
            }
        CustodyKeyId = checkpointKey
        CustodyPublicKeySha256 = digest
        CheckedAt = DateTimeOffset.UtcNow
        ValidUntil = DateTimeOffset.UtcNow.AddMinutes(5.)
    }

let private index =
    {
        InstallationId = primaryId
        LineageId = lineageId
        Epoch = 1L
        CycleId = cycleId
        BackupCaptureSequence = report.BackupCaptureSequence
        BackupCaptureHash = report.BackupCaptureHash
        PublicationManifestSha256 = digest
        ArchiveRoot = "/synthetic/archive"
        ArchiveSetId = report.ArchiveCustody.ObjectId
        ArchiveObjects = []
        PrimaryCaptureWalEndpoint = "0/AAA"
        WitnessCaptureWalEndpoint = "0/DDD"
        PrimaryRegisteredWalHorizon = report.PrimaryRegisteredWalHorizon
        WitnessRegisteredWalHorizon = report.WitnessRegisteredWalHorizon
        PrimaryWalEndpoint = facts.PrimaryWalEndpoint
        WitnessWalEndpoint = facts.WitnessWalEndpoint
        CheckpointRoot = "/synthetic"
        CheckpointFile = "/synthetic/checkpoint"
        CheckpointSignatureFile = "/synthetic/checkpoint.sig"
        ManifestFile = "/synthetic/manifest"
        ManifestSignatureFile = "/synthetic/manifest.sig"
        ManifestSha256 = digest
        BarrierFile = "/synthetic/barrier"
        BarrierSignatureFile = "/synthetic/barrier.sig"
        InventoryRoot = "/synthetic"
        InventorySnapshotFile = "/synthetic/inventory"
        InventorySnapshotSignatureFile = "/synthetic/inventory.sig"
        CheckpointSignerKeyId = checkpointKey
        CheckpointObjectId = report.CheckpointCustody.ObjectId
    }

let private matches candidateReport candidateIndex candidateFacts candidatePublication =
    DatabaseRestoreLive.matchesLivePair
        candidatePublication
        candidateReport
        candidateIndex
        candidateFacts
        digest

let private assertDivergentFacts () =
    let divergent =
        [
            { facts with
                PrimarySystemId = "333333333333"
            }
            { facts with
                WitnessSystemId = "444444444444"
            }
            { facts with PrimaryTimeline = 4L }
            { facts with WitnessTimeline = 4L }
            { facts with WitnessCutoff = 7L }
            { facts with
                WitnessCutoffHash = String('b', 64)
            }
            { facts with AuthorityRevision = 5L }
            { facts with PendingIntents = 1L }
        ]

    for changed in divergent do
        Expect.isFalse
            (matches report index changed publication)
            "Older or changed cluster evidence cannot match the signed report."

let private assertBoundedEvidence () =
    Expect.isFalse
        (matches
            report
            { index with
                PrimaryWalEndpoint = "0/AC0"
            }
            facts
            publication)
        "A signed replay cutoff ahead of the live primary is refused."

    Expect.isTrue
        (matches
            report
            index
            { facts with
                PrimaryWalEndpoint = "0/AC0"
            }
            publication)
        "Normal WAL advancement past the signed restored cutoff is admissible."

    Expect.isFalse
        (matches
            report
            index
            facts
            { publication with
                VerifierBinarySha256 = String('b', 64)
            })
        "An owner-selected verifier binary is refused."

let private divergentPair =
    testCase "[CC-BACKUP-001] stale or divergent restore pair facts refuse recheck" (fun _ ->
        Expect.isTrue
            (matches report index facts publication)
            "Exact synthetic facts satisfy only the pure comparison, not live qualification."

        assertDivergentFacts ()
        assertBoundedEvidence ())

let tests = testList "Restore report pair binding" [ divergentPair ]
