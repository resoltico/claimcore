module internal ClaimCore.IntegrationTests.BackupHealthRuntimeActorRaceDocuments

open System
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.BackupHealthSourceMaterial

let private baseCopy (value: BackupHealthBase) =
    let document = fields ()
    put document "copyId" (value.CopyId.ToString("D"))
    put document "revision" value.Revision
    put document "physicalReceiptSha256" value.PhysicalReceiptSha256
    put document "verifiedAt" (stamp value.VerifiedAt)
    document

let private wal (value: BackupHealthWal) =
    let document = fields ()
    put document "copyIds" (value.CopyIds |> List.map (fun id -> id.ToString("D")))
    put document "registeredHorizon" value.RegisteredHorizon
    put document "archiveInspectionSha256" value.ArchiveInspectionSha256
    put document "verifiedAt" (stamp value.VerifiedAt)
    document

let private checkpoint (value: BackupHealthCheckpoint) =
    let document = fields ()
    put document "sequence" value.Sequence
    put document "hash" value.Hash
    put document "objectSha256" value.ObjectSha256
    put document "verifiedAt" (stamp value.VerifiedAt)
    document

let private restored (value: BackupHealthTestRestore) =
    let document = fields ()
    put document "reportSha256" value.ReportSha256
    put document "witnessCutoff" value.WitnessCutoff
    put document "witnessCutoffHash" value.WitnessCutoffHash
    put document "verifiedAt" (stamp value.VerifiedAt)
    document

let private writerFence (value: BackupHealthWriterFence) =
    let document = fields ()
    put document "kind" value.Kind
    put document "handoffId" (value.HandoffId |> Option.map (fun id -> id.ToString("D")))
    put document "w1Sequence" value.W1Sequence
    put document "w1Hash" value.W1Hash
    put document "activationSequence" value.ActivationSequence
    put document "activationHash" value.ActivationHash
    put document "oldGeneration" value.OldGeneration
    put document "newGeneration" value.NewGeneration
    document

let certificate (value: BackupHealthClaims) =
    let document = fields ()
    put document "format" "claimcore-backup-health-1"
    put document "source" "ClaimCore.Database"
    put document "scope" "full"
    put document "installationId" (value.InstallationId.ToString("D"))
    put document "lineageId" (value.LineageId.ToString("D"))
    put document "epoch" value.Epoch
    put document "writerGeneration" value.WriterGeneration
    put document "policyId" value.PolicyId
    put document "authorityRevision" value.AuthorityRevision
    put document "witnessTipSequence" value.WitnessTipSequence
    put document "witnessTipHash" value.WitnessTipHash
    put document "checkedAt" (stamp value.CheckedAt)
    put document "validUntil" (stamp value.ValidUntil)
    put document "maximumBackupAgeSeconds" value.MaximumBackupAgeSeconds
    put document "maximumWalLagSeconds" value.MaximumWalLagSeconds
    put document "maximumCheckpointAgeSeconds" value.MaximumCheckpointAgeSeconds
    put document "maximumRestoreTestAgeSeconds" value.MaximumRestoreTestAgeSeconds
    put document "restoreHorizonSeconds" value.RestoreHorizonSeconds
    put document "primarySystemId" value.PrimarySystemId
    put document "primaryTimeline" value.PrimaryTimeline
    put document "witnessSystemId" value.WitnessSystemId
    put document "witnessTimeline" value.WitnessTimeline
    put document "primaryBase" (baseCopy value.PrimaryBase)
    put document "witnessBase" (baseCopy value.WitnessBase)
    put document "primaryWal" (wal value.PrimaryWal)
    put document "witnessWal" (wal value.WitnessWal)
    put document "checkpoint" (checkpoint value.Checkpoint)
    put document "testRestore" (restored value.TestRestore)
    put document "knownCopyInventorySha256" value.KnownCopyInventorySha256
    put document "artifactCutoffSequence" value.ArtifactCutoffSequence
    put document "writerFence" (writerFence value.WriterFence)
    put document "signerKeyId" (value.SignerKeyId.ToString("D"))
    put document "signerHolderActorId" (value.SignerHolderActorId.ToString("D"))
    canonical document
