namespace ClaimCore.Database

open System
open System.Text.Json
open ClaimCore.Postgres

module internal DatabaseBackupHealthEvidenceClaims =
    open BackupHealthFields

    let private fields =
        [
            "format"
            "cycleId"
            "leaseId"
            "captureNonce"
            "captureReceiptSha256"
            "backupCaptureSequence"
            "backupCaptureHash"
            "installationId"
            "lineageId"
            "epoch"
            "writerGeneration"
            "policyId"
            "authorityRevision"
            "witnessTipSequence"
            "witnessTipHash"
            "knownCopyInventorySha256"
            "artifactCutoffSequence"
            "checkedAt"
            "validUntil"
            "objects"
            "checkpoint"
            "testRestore"
        ]

    let private objects (root: JsonElement) =
        let raw = root.GetProperty("objects")

        if
            raw.ValueKind <> JsonValueKind.Array
            || raw.GetArrayLength() < 4
            || raw.GetArrayLength() > 1000
        then
            invalidOp "Independent backup health BASE/WAL object count is invalid."

        let values =
            raw.EnumerateArray()
            |> Seq.map DatabaseBackupHealthEvidenceParts.archiveObject
            |> Seq.toList

        let pair cluster kind =
            values |> List.filter (fun item -> item.Cluster = cluster && item.Kind = kind)

        let ids = values |> List.map _.CopyId

        if
            (pair "PRIMARY" "BASE").Length <> 1
            || (pair "WITNESS" "BASE").Length <> 1
            || (pair "PRIMARY" "WAL").IsEmpty
            || (pair "WITNESS" "WAL").IsEmpty
            || (ids |> Set.ofList |> Set.count) <> values.Length
            || (values |> List.map _.RelativePath |> Set.ofList |> Set.count) <> values.Length
            || (values
                |> List.choose (fun item ->
                    item.WalSegment |> Option.map (fun name -> item.Cluster, name))
                |> Set.ofList
                |> Set.count)
               <> values.Length - 2
        then
            invalidOp "Independent backup health object set is ambiguous."

        values

    let private boundAuthority (value: BackupHealthEvidence) =
        if
            value.Epoch < 1L
            || value.WriterGeneration < 1L
            || value.AuthorityRevision < 1L
            || value.WitnessTipSequence < 0L
            || value.BackupCaptureSequence < 1L
            || value.BackupCaptureSequence > value.WitnessTipSequence
            || value.ArtifactCutoffSequence < 0L
            || value.ArtifactCutoffSequence > value.WitnessTipSequence
            || value.Checkpoint.Sequence > value.WitnessTipSequence
            || value.Checkpoint.Sequence <> value.BackupCaptureSequence
            || value.Checkpoint.Hash <> value.BackupCaptureHash
            || value.TestRestore.WitnessCutoff > value.WitnessTipSequence
        then
            invalidOp "Independent backup health authority cutoff is invalid."

    let private boundCluster
        (baseCopy: BackupHealthArchiveObject)
        (wal: BackupHealthArchiveObject list)
        expectedCopyId
        expectedHorizon
        expectedSystem
        expectedTimeline
        =
        if
            baseCopy.CopyId <> expectedCopyId
            || baseCopy.PostgresSystemId <> expectedSystem
            || baseCopy.Timeline <> expectedTimeline
            || (wal
                |> List.exists (fun item ->
                    item.WalHorizon <> expectedHorizon
                    || item.PostgresSystemId <> baseCopy.PostgresSystemId
                    || item.Timeline <> baseCopy.Timeline
                    || item.WalSegmentBytes <> baseCopy.WalSegmentBytes))
        then
            invalidOp "Independent backup health cluster and WAL prefix diverge."

    let private bound (value: BackupHealthEvidence) =
        boundAuthority value

        let copy cluster kind =
            value.Objects
            |> List.find (fun item -> item.Cluster = cluster && item.Kind = kind)

        let primaryBase = copy "PRIMARY" "BASE"
        let witnessBase = copy "WITNESS" "BASE"

        let wal cluster =
            value.Objects
            |> List.filter (fun item -> item.Cluster = cluster && item.Kind = "WAL")

        let primaryWal = wal "PRIMARY"
        let witnessWal = wal "WITNESS"
        let restored = value.TestRestore

        if primaryBase.PostgresSystemId = witnessBase.PostgresSystemId then
            invalidOp "Independent backup health clusters share physical identity."

        boundCluster
            primaryBase
            primaryWal
            restored.PrimaryBaseCopyId
            restored.PrimaryWalHorizon
            restored.PrimarySystemId
            restored.PrimaryTimeline

        boundCluster
            witnessBase
            witnessWal
            restored.WitnessBaseCopyId
            restored.WitnessWalHorizon
            restored.WitnessSystemId
            restored.WitnessTimeline

    let private decoded (root: JsonElement) now =
        if
            not (BackupHealthCanonical.exact root fields)
            || text root "format" <> "claimcore-backup-health-source-1"
        then
            invalidOp "Independent backup health evidence format is invalid."

        let checkedAt = instant root "checkedAt"
        let validUntil = instant root "validUntil"

        if checkedAt > now || now >= validUntil || validUntil > checkedAt.AddMinutes(5.) then
            invalidOp "Independent backup health evidence is expired."

        let value =
            {
                CycleId = uuid root "cycleId"
                LeaseId = uuid root "leaseId"
                CaptureNonce = sha root "captureNonce"
                CaptureReceiptSha256 = sha root "captureReceiptSha256"
                BackupCaptureSequence = number root "backupCaptureSequence"
                BackupCaptureHash = sha root "backupCaptureHash"
                InstallationId = uuid root "installationId"
                LineageId = uuid root "lineageId"
                Epoch = number root "epoch"
                WriterGeneration = number root "writerGeneration"
                PolicyId = policyId root
                AuthorityRevision = number root "authorityRevision"
                WitnessTipSequence = number root "witnessTipSequence"
                WitnessTipHash = sha root "witnessTipHash"
                KnownCopyInventorySha256 = sha root "knownCopyInventorySha256"
                ArtifactCutoffSequence = number root "artifactCutoffSequence"
                CheckedAt = checkedAt
                ValidUntil = validUntil
                Objects = objects root
                Checkpoint =
                    DatabaseBackupHealthEvidenceParts.checkpoint (root.GetProperty("checkpoint"))
                TestRestore =
                    DatabaseBackupHealthEvidenceParts.restored (root.GetProperty("testRestore"))
            }

        bound value
        value

    let parse source now =
        match BackupHealthCanonical.parse 131072 source with
        | None -> None
        | Some root ->
            try
                Some(decoded root now)
            with _ ->
                None

    /// Historical plan review authenticates the original issuance window, without
    /// treating an expired source as a fresh health certificate.
    let parseHistorical source now =
        match BackupHealthCanonical.parse 131072 source with
        | None -> None
        | Some root ->
            try
                let issued = instant root "checkedAt"

                if issued > now then None else Some(decoded root issued)
            with _ ->
                None
