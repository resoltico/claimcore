namespace ClaimCore.Database

open System
open System.Security.Cryptography
open ClaimCore.Postgres

module internal DatabaseBackupHealthBinding =
    let private item (value: BackupHealthEvidence) cluster kind =
        value.Objects |> List.find (fun row -> row.Cluster = cluster && row.Kind = kind)

    let private baseMatches (claim: BackupHealthBase) (source: BackupHealthArchiveObject) =
        claim.CopyId = source.CopyId
        && claim.Revision = source.Revision
        && claim.PhysicalReceiptSha256 = source.PhysicalReceiptSha256
        && claim.VerifiedAt = source.VerifiedAt

    let private walMatches
        (claim: BackupHealthWal)
        (source: BackupHealthArchiveObject list)
        digest
        checkedAt
        =
        source |> List.forall (fun item -> item.WalHorizon = claim.RegisteredHorizon)
        && claim.CopyIds = (source |> List.map _.CopyId |> List.sort)
        && claim.ArchiveInspectionSha256 = digest
        && claim.VerifiedAt = checkedAt

    let private policyMatches (policy: BackupHealthPolicy) (claim: BackupHealthClaims) =
        claim.PolicyId = policy.PolicyId
        && claim.MaximumBackupAgeSeconds = policy.MaximumBackupAgeSeconds
        && claim.MaximumWalLagSeconds = policy.MaximumWalLagSeconds
        && claim.MaximumCheckpointAgeSeconds = policy.MaximumCheckpointAgeSeconds
        && claim.MaximumRestoreTestAgeSeconds = policy.MaximumRestoreTestAgeSeconds
        && claim.RestoreHorizonSeconds = policy.RestoreHorizonSeconds

    let private sourceIdentity (evidence: BackupHealthEvidence) (claim: BackupHealthClaims) =
        claim.InstallationId = evidence.InstallationId
        && claim.LineageId = evidence.LineageId
        && claim.Epoch = evidence.Epoch
        && claim.WriterGeneration = evidence.WriterGeneration
        && claim.AuthorityRevision = evidence.AuthorityRevision
        && claim.WitnessTipSequence = evidence.WitnessTipSequence
        && claim.WitnessTipHash = evidence.WitnessTipHash
        && claim.KnownCopyInventorySha256 = evidence.KnownCopyInventorySha256
        && claim.ArtifactCutoffSequence = evidence.ArtifactCutoffSequence

    let private recoveryIdentity (evidence: BackupHealthEvidence) (claim: BackupHealthClaims) =
        let checkpoint = evidence.Checkpoint
        let restored = evidence.TestRestore

        claim.Checkpoint.Sequence = checkpoint.Sequence
        && claim.Checkpoint.Hash = checkpoint.Hash
        && claim.Checkpoint.ObjectSha256 = checkpoint.ObjectSha256
        && claim.Checkpoint.VerifiedAt = checkpoint.VerifiedAt
        && claim.TestRestore.ReportSha256 = restored.ReportSha256
        && claim.TestRestore.WitnessCutoff = restored.WitnessCutoff
        && claim.TestRestore.WitnessCutoffHash = restored.WitnessCutoffHash
        && claim.TestRestore.VerifiedAt = restored.VerifiedAt
        && claim.PrimarySystemId = restored.PrimarySystemId
        && claim.PrimaryTimeline = restored.PrimaryTimeline
        && claim.WitnessSystemId = restored.WitnessSystemId
        && claim.WitnessTimeline = restored.WitnessTimeline

    let verify
        (policy: BackupHealthPolicy)
        (evidence: BackupHealthEvidence)
        (claim: BackupHealthClaims)
        (sourceBytes: byte array)
        (now: DateTimeOffset)
        =
        let sourceSha = SHA256.HashData(sourceBytes) |> Convert.ToHexStringLower
        let primaryBase = item evidence "PRIMARY" "BASE"
        let witnessBase = item evidence "WITNESS" "BASE"

        let wal cluster =
            evidence.Objects
            |> List.filter (fun row -> row.Cluster = cluster && row.Kind = "WAL")

        let primaryWal = wal "PRIMARY"
        let witnessWal = wal "WITNESS"

        if
            not (policyMatches policy claim)
            || not (sourceIdentity evidence claim)
            || claim.CheckedAt < evidence.CheckedAt
            || claim.CheckedAt > now
            || claim.ValidUntil > evidence.ValidUntil
            || claim.ValidUntil > claim.CheckedAt.AddSeconds(90.)
            || not (baseMatches claim.PrimaryBase primaryBase)
            || not (baseMatches claim.WitnessBase witnessBase)
            || not (walMatches claim.PrimaryWal primaryWal sourceSha evidence.CheckedAt)
            || not (walMatches claim.WitnessWal witnessWal sourceSha evidence.CheckedAt)
        then
            invalidOp "Backup health certificate differs from independent source or policy."

        if not (recoveryIdentity evidence claim) then
            invalidOp "Backup health checkpoint or test restore differs from source."
