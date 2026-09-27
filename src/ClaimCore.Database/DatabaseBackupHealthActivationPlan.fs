namespace ClaimCore.Database

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.Postgres

/// Owner approvals name durable recovery facts and minimum WAL coverage. Current
/// authority, inventory, time and certificate bytes deliberately remain outside.
module internal DatabaseBackupHealthActivationPlan =
    let private objectFor (source: BackupHealthEvidence) cluster kind =
        source.Objects
        |> List.find (fun item -> item.Cluster = cluster && item.Kind = kind)

    let private horizon (source: BackupHealthEvidence) cluster =
        let wal =
            source.Objects
            |> List.find (fun item -> item.Cluster = cluster && item.Kind = "WAL")

        wal.WalHorizon

    let private position (value: string) =
        let halves = value.Split('/')

        if halves.Length <> 2 then
            invalidOp "Activation WAL horizon is invalid."

        (Convert.ToUInt64(halves[0], 16) <<< 32) ||| Convert.ToUInt64(halves[1], 16)

    let private baseFields
        (value: SortedDictionary<string, objnull>)
        (prefix: string)
        (copy: BackupHealthArchiveObject)
        systemId
        timeline
        =
        let put suffix item = value.Add(prefix + suffix, box item)
        put "BaseCiphertextSha256" copy.CiphertextSha256
        put "BaseCopyId" (copy.CopyId.ToString("D"))
        put "BasePhysicalReceiptSha256" copy.PhysicalReceiptSha256
        put "BaseRevision" copy.Revision
        put "BaseWalHorizon" copy.WalHorizon
        put "SystemId" systemId
        put "Timeline" timeline
        put "WalSegmentBytes" copy.WalSegmentBytes

    let private canonical
        policySha
        rootSha
        minimumArtifact
        minimumPrimary
        minimumWitness
        (source: BackupHealthEvidence)
        =
        let value = SortedDictionary<string, objnull>(StringComparer.Ordinal)
        let put name item = value.Add(name, box item)
        let primary = objectFor source "PRIMARY" "BASE"
        let witness = objectFor source "WITNESS" "BASE"
        let checkpoint = source.Checkpoint
        let restored = source.TestRestore

        put "minimumArtifactCutoffSequence" minimumArtifact
        put "backupCaptureHash" source.BackupCaptureHash
        put "backupCaptureSequence" source.BackupCaptureSequence
        put "captureNonce" source.CaptureNonce
        put "captureReceiptSha256" source.CaptureReceiptSha256
        put "checkpointHash" checkpoint.Hash
        put "checkpointObjectSha256" checkpoint.ObjectSha256
        put "checkpointSequence" checkpoint.Sequence
        put "cycleId" (source.CycleId.ToString("D"))
        put "epoch" source.Epoch
        put "format" "claimcore-real-data-activation-plan-1"
        put "installationId" (source.InstallationId.ToString("D"))
        put "leaseId" (source.LeaseId.ToString("D"))
        put "lineageId" (source.LineageId.ToString("D"))
        put "minimumPrimaryWalHorizon" minimumPrimary
        put "minimumWitnessWalHorizon" minimumWitness
        put "policySha256" policySha
        baseFields value "primary" primary restored.PrimarySystemId restored.PrimaryTimeline
        put "publicationRootKeySha256" rootSha
        put "testRestoreFullAuditSha256" restored.FullAuditSha256
        put "testRestoreReportSha256" restored.ReportSha256
        put "testRestoreWitnessCutoff" restored.WitnessCutoff
        put "testRestoreWitnessCutoffHash" restored.WitnessCutoffHash
        baseFields value "witness" witness restored.WitnessSystemId restored.WitnessTimeline
        put "writerGeneration" source.WriterGeneration
        Array.append (JsonSerializer.SerializeToUtf8Bytes(value)) [| byte '\n' |]

    let create (profile: ReviewedDeploymentProfile) (source: BackupHealthEvidence) =
        DatabaseBackupHealthWalCoverage.verify source
        let minimumPrimary = horizon source "PRIMARY"
        let minimumWitness = horizon source "WITNESS"

        let rootSha =
            SHA256.HashData(profile.PublicationRootKey) |> Convert.ToHexStringLower

        let bytes =
            canonical
                profile.BackupHealthPolicySha256
                rootSha
                source.ArtifactCutoffSequence
                minimumPrimary
                minimumWitness
                source

        let primary = objectFor source "PRIMARY" "BASE"
        let witness = objectFor source "WITNESS" "BASE"

        {
            Canonical = bytes
            PlanSha256 = SHA256.HashData(bytes) |> Convert.ToHexStringLower
            InstallationId = source.InstallationId
            LineageId = source.LineageId
            Epoch = source.Epoch
            WriterGeneration = source.WriterGeneration
            PolicySha256 = profile.BackupHealthPolicySha256
            PublicationRootSha256 = rootSha
            CycleId = source.CycleId
            LeaseId = source.LeaseId
            CaptureReceiptSha256 = source.CaptureReceiptSha256
            PrimaryBaseCopyId = primary.CopyId
            WitnessBaseCopyId = witness.CopyId
            PrimaryBasePhysicalReceiptSha256 = primary.PhysicalReceiptSha256
            WitnessBasePhysicalReceiptSha256 = witness.PhysicalReceiptSha256
            PrimaryBaseWalHorizon = primary.WalHorizon
            WitnessBaseWalHorizon = witness.WalHorizon
            PrimaryWalSegmentBytes = primary.WalSegmentBytes
            WitnessWalSegmentBytes = witness.WalSegmentBytes
            CheckpointObjectSha256 = source.Checkpoint.ObjectSha256
            TestRestoreReportSha256 = source.TestRestore.ReportSha256
            TestRestoreFullAuditSha256 = source.TestRestore.FullAuditSha256
            MinimumArtifactCutoffSequence = source.ArtifactCutoffSequence
            MinimumPrimaryWalHorizon = minimumPrimary
            MinimumWitnessWalHorizon = minimumWitness
        }

    let meets (plan: BackupHealthActivationPlan) (source: BackupHealthEvidence) =
        let bytes =
            canonical
                plan.PolicySha256
                plan.PublicationRootSha256
                plan.MinimumArtifactCutoffSequence
                plan.MinimumPrimaryWalHorizon
                plan.MinimumWitnessWalHorizon
                source

        source.ArtifactCutoffSequence >= plan.MinimumArtifactCutoffSequence
        && position (horizon source "PRIMARY") >= position plan.MinimumPrimaryWalHorizon
        && position (horizon source "WITNESS") >= position plan.MinimumWitnessWalHorizon
        && CryptographicOperations.FixedTimeEquals(bytes.AsSpan(), plan.Canonical.AsSpan())
