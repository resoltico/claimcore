namespace ClaimCore.Postgres

open System
open System.Text.Json

/// Exact, canonical signed mutation-health certificate shape. Signature and live facts
/// are verified separately by the owner issuer and runtime admission reader.
module internal BackupHealthCertificate =
    open BackupHealthFields

    let private fields =
        [
            "format"
            "source"
            "scope"
            "installationId"
            "lineageId"
            "epoch"
            "writerGeneration"
            "policyId"
            "authorityRevision"
            "witnessTipSequence"
            "witnessTipHash"
            "checkedAt"
            "validUntil"
            "maximumBackupAgeSeconds"
            "maximumWalLagSeconds"
            "maximumCheckpointAgeSeconds"
            "maximumRestoreTestAgeSeconds"
            "restoreHorizonSeconds"
            "primarySystemId"
            "primaryTimeline"
            "witnessSystemId"
            "witnessTimeline"
            "primaryBase"
            "witnessBase"
            "primaryWal"
            "witnessWal"
            "checkpoint"
            "testRestore"
            "knownCopyInventorySha256"
            "artifactCutoffSequence"
            "writerFence"
            "signerKeyId"
            "signerHolderActorId"
        ]

    let private age checkedAt maximum observed =
        observed <= checkedAt
        && checkedAt - observed <= TimeSpan.FromSeconds(float maximum)

    let private temporal (value: BackupHealthClaims) now =
        if
            value.CheckedAt > now
            || now >= value.ValidUntil
            || value.ValidUntil > value.CheckedAt.AddMinutes(5.)
            || not (age value.CheckedAt value.MaximumBackupAgeSeconds value.PrimaryBase.VerifiedAt)
            || not (age value.CheckedAt value.MaximumBackupAgeSeconds value.WitnessBase.VerifiedAt)
            || not (age value.CheckedAt value.MaximumWalLagSeconds value.PrimaryWal.VerifiedAt)
            || not (age value.CheckedAt value.MaximumWalLagSeconds value.WitnessWal.VerifiedAt)
            || not (
                age value.CheckedAt value.MaximumCheckpointAgeSeconds value.Checkpoint.VerifiedAt
            )
            || not (
                age value.CheckedAt value.MaximumRestoreTestAgeSeconds value.TestRestore.VerifiedAt
            )
        then
            invalidOp "Backup health evidence is stale."

    let private authority (value: BackupHealthClaims) =
        if
            value.PrimarySystemId = value.WitnessSystemId
            || value.PrimaryBase.CopyId = value.WitnessBase.CopyId
            || value.Epoch < 1L
            || value.WriterGeneration < 1L
            || value.AuthorityRevision < 1L
            || value.WitnessTipSequence < 0L
            || value.Checkpoint.Sequence > value.WitnessTipSequence
            || value.TestRestore.WitnessCutoff > value.WitnessTipSequence
            || value.ArtifactCutoffSequence < 0L
            || value.ArtifactCutoffSequence > value.WitnessTipSequence
        then
            invalidOp "Backup health authority binding is invalid."

    let private decode (root: JsonElement) =
        if
            not (BackupHealthCanonical.exact root fields)
            || text root "format" <> "claimcore-backup-health-1"
            || text root "source" <> "ClaimCore.Database"
            || text root "scope" <> "full"
        then
            invalidOp "Backup health certificate format is invalid."

        let generation = number root "writerGeneration"
        let tip = number root "witnessTipSequence"
        let maximum = 365L * 86400L

        {
            InstallationId = uuid root "installationId"
            LineageId = uuid root "lineageId"
            Epoch = number root "epoch"
            WriterGeneration = generation
            PolicyId = policyId root
            AuthorityRevision = number root "authorityRevision"
            WitnessTipSequence = tip
            WitnessTipHash = sha root "witnessTipHash"
            CheckedAt = instant root "checkedAt"
            ValidUntil = instant root "validUntil"
            MaximumBackupAgeSeconds = positive maximum (number root "maximumBackupAgeSeconds")
            MaximumWalLagSeconds = positive maximum (number root "maximumWalLagSeconds")
            MaximumCheckpointAgeSeconds =
                positive maximum (number root "maximumCheckpointAgeSeconds")
            MaximumRestoreTestAgeSeconds =
                positive maximum (number root "maximumRestoreTestAgeSeconds")
            RestoreHorizonSeconds = positive maximum (number root "restoreHorizonSeconds")
            PrimarySystemId = systemId root "primarySystemId"
            PrimaryTimeline = positive (int64 Int32.MaxValue) (number root "primaryTimeline")
            WitnessSystemId = systemId root "witnessSystemId"
            WitnessTimeline = positive (int64 Int32.MaxValue) (number root "witnessTimeline")
            PrimaryBase = BackupHealthDocumentParts.baseCopy (root.GetProperty("primaryBase"))
            WitnessBase = BackupHealthDocumentParts.baseCopy (root.GetProperty("witnessBase"))
            PrimaryWal = BackupHealthDocumentParts.wal (root.GetProperty("primaryWal"))
            WitnessWal = BackupHealthDocumentParts.wal (root.GetProperty("witnessWal"))
            Checkpoint = BackupHealthDocumentParts.checkpoint (root.GetProperty("checkpoint"))
            TestRestore = BackupHealthDocumentParts.restored (root.GetProperty("testRestore"))
            KnownCopyInventorySha256 = sha root "knownCopyInventorySha256"
            ArtifactCutoffSequence = number root "artifactCutoffSequence"
            WriterFence =
                BackupHealthDocumentParts.fence (root.GetProperty("writerFence")) generation tip
            SignerKeyId = uuid root "signerKeyId"
            SignerHolderActorId = uuid root "signerHolderActorId"
        }

    let parse (source: byte array) now =
        match BackupHealthCanonical.parse 65536 source with
        | None -> None
        | Some root ->
            try
                let value = decode root
                authority value
                temporal value now
                Some value
            with _ ->
                None

    /// Historical publication readback verifies the original signed validity window;
    /// it never grants current backup health or real-data mutation admission.
    let parseHistorical (source: byte array) now =
        match BackupHealthCanonical.parse 65536 source with
        | None -> None
        | Some root ->
            try
                let value = decode root

                if value.CheckedAt > now then
                    None
                else
                    authority value
                    temporal value value.CheckedAt
                    Some value
            with _ ->
                None
