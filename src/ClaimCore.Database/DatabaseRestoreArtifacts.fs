namespace ClaimCore.Database

open System
open System.IO
open System.Text.Json
open Npgsql
open ClaimCore.HostSecurity
open ClaimCore.Postgres

module internal DatabaseRestoreArtifacts =
    let private text name (root: JsonElement) =
        DatabaseRestoreCanonical.text name root
        |> Option.ofObj
        |> Option.defaultValue ""

    let private identity (report: RestoreReportClaims) (root: JsonElement) =
        DatabaseRestoreSignedEvidence.exactText
            "installationId"
            root
            (report.InstallationId.ToString("D"))

        DatabaseRestoreSignedEvidence.exactText "lineageId" root (report.LineageId.ToString("D"))
        DatabaseRestoreSignedEvidence.exactNumber "epoch" root report.Epoch
        DatabaseRestoreSignedEvidence.exactText "cycleId" root (report.CycleId.ToString("D"))

    let private cutoff (report: RestoreReportClaims) (root: JsonElement) =
        DatabaseRestoreSignedEvidence.exactNumber "witnessCutoff" root report.WitnessCutoff
        DatabaseRestoreSignedEvidence.exactText "witnessCutoffHash" root report.WitnessCutoffHash

    let verifyManifest
        (index: RestoreEvidenceIndex)
        (report: RestoreReportClaims)
        (reportKey: byte array)
        =
        use document =
            DatabaseRestoreSignedEvidence.verifyFile
                131072
                index.ManifestSha256
                reportKey
                index.ManifestFile
                index.ManifestSignatureFile

        let root = document.RootElement
        identity report root

        // Source capture may be online; a later signed barrier, registered WAL and complete
        // restored-pair audit establish the candidate's coherent cutoff independently.
        if
            text "format" root <> "claimcore-backup-cycle-1"
            || text "consistencyScope" root <> "unfenced-capture"
        then
            invalidOp "Signed backup source scope is unsupported."

        let tip = root.GetProperty("witnessCheckpoint")
        DatabaseRestoreSignedEvidence.exactNumber "sequence" tip report.BackupCaptureSequence
        DatabaseRestoreSignedEvidence.exactText "hash" tip report.BackupCaptureHash

        let copyIds = root.GetProperty("copyIds")

        if
            not (DatabaseRestoreCanonical.exactProperties [ "primary"; "witness" ] copyIds)
            || text "primary" copyIds = text "witness" copyIds
        then
            invalidOp "Signed backup manifest copy identities are invalid."

        let copyId name =
            let raw = text name copyIds

            match Guid.TryParseExact(raw, "D") with
            | true, value when value <> Guid.Empty && value.ToString("D") = raw -> value
            | _ -> invalidOp "Signed backup manifest copy ID is invalid."

        copyId "primary", copyId "witness"

    let verifyInventory
        (index: RestoreEvidenceIndex)
        (report: RestoreReportClaims)
        (checkpointKey: byte array)
        (facts: RestoredPairFacts)
        =
        match PrivateFileService.requirePrivateDirectory index.InventoryRoot with
        | Ok() -> ()
        | Error _ -> invalidOp "Independent inventory root is not owner-private."

        use document =
            DatabaseRestoreSignedEvidence.verifyFile
                131072
                report.SignedInventoryFileSha256
                checkpointKey
                index.InventorySnapshotFile
                index.InventorySnapshotSignatureFile

        let root = document.RootElement
        identity report root
        cutoff report root

        if
            text "format" root <> "claimcore-managed-inventory-snapshot-1"
            || text "snapshotSha256" root <> facts.ManagedCopySnapshotSha256
            || DatabaseRestoreCanonical.number "managedCopyCount" root
               <> facts.ManagedCopyCount
        then
            invalidOp "Independent managed-copy inventory differs from restored primary."

    let private archiveShape
        (reader: Data.Common.DbDataReader)
        (index: RestoreEvidenceIndex)
        (item: RestoreArchiveObject)
        =
        let walEnd =
            if reader.IsDBNull(9) then
                None
            else
                Some(reader.GetString(9))

        let segment =
            if reader.IsDBNull(10) then
                None
            else
                Some(reader.GetString(10))

        let segmentBytes =
            if reader.IsDBNull(11) then
                None
            else
                Some(reader.GetInt32(11))

        let expectedCapture =
            if item.Cluster = "PRIMARY" then
                index.PrimaryCaptureWalEndpoint
            else
                index.WitnessCaptureWalEndpoint

        if item.Kind = "BASE" then
            walEnd = Some expectedCapture && segment.IsNone && segmentBytes.IsSome
        else
            walEnd.IsNone
            && segment = item.WalSegment
            && segmentBytes = item.WalSegmentBytes

    let private registeredArchiveCopy
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (index: RestoreEvidenceIndex)
        (report: RestoreReportClaims)
        (item: RestoreArchiveObject)
        =
        use command =
            new NpgsqlCommand(
                "SELECT cluster_name,copy_kind,state,ciphertext_sha256,ciphertext_bytes,"
                + "postgres_system_id,timeline,witness_cutoff_sequence,witness_cutoff_hash,"
                + "wal_end_lsn,wal_segment,wal_segment_bytes,"
                + "last_verified_at,verification_proof_sha256 "
                + "FROM claimcore.managed_copies WHERE copy_id=@copy",
                connection,
                transaction
            )

        Sql.uuid command "copy" item.CopyId
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Signed archive object has no registered managed copy."

        let expectedSystem, expectedTimeline =
            if item.Cluster = "PRIMARY" then
                report.PrimarySystemId, report.PrimaryTimeline
            else
                report.WitnessSystemId, report.WitnessTimeline

        let shapeMatches = archiveShape reader index item

        let commonMatches =
            reader.GetString(0) = item.Cluster
            && reader.GetString(1) = item.Kind
            && reader.GetString(2) = "RETAINED"
            && Convert.ToHexStringLower(reader.GetFieldValue<byte array>(3)) = item.Sha256
            && reader.GetInt64(4) = item.Bytes
            && reader.GetString(5) = expectedSystem
            && int64 (reader.GetInt32(6)) = expectedTimeline
            && reader.GetInt64(7) = report.BackupCaptureSequence
            && Convert.ToHexStringLower(reader.GetFieldValue<byte array>(8)) =
                report.BackupCaptureHash
            && not (reader.IsDBNull(12) || reader.IsDBNull(13))
            && reader.GetFieldValue<byte array>(13).Length = 32

        if not (commonMatches && shapeMatches) || reader.Read() then
            invalidOp "Registered managed copy diverges from signed archive bytes."

    let verifyArchiveFilesAtRoot
        configured
        (index: RestoreEvidenceIndex)
        (report: RestoreReportClaims)
        primaryBaseCopy
        witnessBaseCopy
        =
        if configured <> index.ArchiveRoot then
            invalidOp "Restored archive root differs from owner-private configuration."

        match PrivateFileService.requirePrivateDirectory index.ArchiveRoot with
        | Ok() -> ()
        | Error _ -> invalidOp "Restored archive root is not owner-private."

        let objects = DatabaseRestoreArchiveObjects.validate index.ArchiveObjects

        let oneBase cluster copyId =
            objects
            |> List.filter (fun item -> item.Cluster = cluster && item.Kind = "BASE")
            |> function
                | [ item ] when item.CopyId = copyId -> ()
                | _ -> invalidOp "Signed base archive does not bind the backup manifest."

        oneBase "PRIMARY" primaryBaseCopy
        oneBase "WITNESS" witnessBaseCopy

        for item in objects do
            let path = Path.Combine(index.ArchiveRoot, item.RelativePath)

            match PrivateFileService.hashPrivateFile item.Bytes path with
            | Ok(length, digest) when
                length = item.Bytes && Convert.ToHexStringLower(digest) = item.Sha256
                ->
                ()
            | _ -> invalidOp "Signed encrypted archive bytes are missing or changed."

        if report.CheckpointCustody.Sha256 <> report.CheckpointSha256 then
            invalidOp "Checkpoint custody digest differs from signed report."

        match
            PrivateFileService.hashPrivateFile report.CheckpointCustody.Bytes index.CheckpointFile
        with
        | Ok(length, digest) when
            length = report.CheckpointCustody.Bytes
            && Convert.ToHexStringLower(digest) = report.CheckpointCustody.Sha256
            ->
            ()
        | _ -> invalidOp "Independently retained checkpoint bytes are missing or changed."

    let verifyArchive
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (index: RestoreEvidenceIndex)
        (report: RestoreReportClaims)
        primaryBaseCopy
        witnessBaseCopy
        =
        let configured =
            Environment.GetEnvironmentVariable("CLAIMCORE_RESTORE_ARCHIVE_ROOT")
            |> Option.ofObj
            |> Option.defaultValue ""

        verifyArchiveFilesAtRoot configured index report primaryBaseCopy witnessBaseCopy

        for item in index.ArchiveObjects do
            registeredArchiveCopy connection transaction index report item
