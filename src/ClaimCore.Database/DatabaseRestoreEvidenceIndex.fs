namespace ClaimCore.Database

open System
open System.IO

[<NoEquality; NoComparison>]
type internal RestoreEvidenceIndex =
    {
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        CycleId: Guid
        BackupCaptureSequence: int64
        BackupCaptureHash: string
        PublicationManifestSha256: string
        ArchiveRoot: string
        ArchiveSetId: Guid
        ArchiveObjects: RestoreArchiveObject list
        PrimaryCaptureWalEndpoint: string
        WitnessCaptureWalEndpoint: string
        PrimaryRegisteredWalHorizon: string
        WitnessRegisteredWalHorizon: string
        PrimaryWalEndpoint: string
        WitnessWalEndpoint: string
        CheckpointRoot: string
        CheckpointFile: string
        CheckpointSignatureFile: string
        ManifestFile: string
        ManifestSignatureFile: string
        ManifestSha256: string
        BarrierFile: string
        BarrierSignatureFile: string
        InventoryRoot: string
        InventorySnapshotFile: string
        InventorySnapshotSignatureFile: string
        CheckpointSignerKeyId: Guid
        CheckpointObjectId: Guid
    }

module internal DatabaseRestoreEvidenceIndex =
    let private names =
        [
            "format"
            "installationId"
            "lineageId"
            "epoch"
            "cycleId"
            "backupCaptureSequence"
            "backupCaptureHash"
            "publicationManifestSha256"
            "archiveRoot"
            "archiveSetId"
            "archiveObjects"
            "primaryCaptureWalEndpoint"
            "witnessCaptureWalEndpoint"
            "primaryRegisteredWalHorizon"
            "witnessRegisteredWalHorizon"
            "primarySystemId"
            "primaryTimeline"
            "witnessSystemId"
            "witnessTimeline"
            "primaryWalEndpoint"
            "witnessWalEndpoint"
            "checkpointRoot"
            "checkpointFile"
            "checkpointSignatureFile"
            "manifestFile"
            "manifestSignatureFile"
            "manifestSha256"
            "barrierFile"
            "barrierSignatureFile"
            "inventoryRoot"
            "reportSignerKeyId"
            "checkpointSignerKeyId"
            "checkpointObjectId"
            "inventorySnapshotFile"
            "inventorySnapshotSignatureFile"
        ]

    let private text name root =
        DatabaseRestoreCanonical.text name root
        |> Option.ofObj
        |> Option.defaultValue ""

    let private exactId name root =
        let raw = text name root

        match Guid.TryParseExact(raw, "D") with
        | true, id when id <> Guid.Empty && id.ToString("D") = raw -> id
        | _ -> invalidOp "Restore evidence identity is invalid."

    let private sha name root =
        let raw = text name root

        if
            raw.Length <> 64
            || not (
                raw
                |> Seq.forall (fun character ->
                    ('0' <= character && character <= '9')
                    || ('a' <= character && character <= 'f'))
            )
        then
            invalidOp "Restore evidence digest is invalid."

        raw

    let private path name root =
        let value = text name root

        if
            value.Length < 2
            || value.Length > 4096
            || not (Path.IsPathFullyQualified value)
            || (value.Split(Path.DirectorySeparatorChar) |> Array.exists ((=) ".."))
        then
            invalidOp "Restore evidence private path is invalid."

        value

    let private wal name root =
        let raw = text name root
        let segments = raw.Split('/')

        if
            segments.Length <> 2
            || (segments
                |> Array.exists (fun part ->
                    part.Length < 1
                    || part.Length > 8
                    || part
                       |> Seq.exists (fun character ->
                           not (
                               ('0' <= character && character <= '9')
                               || ('A' <= character && character <= 'F')
                           ))))
        then
            invalidOp "Restore evidence WAL endpoint is invalid."

        raw

    let private project root =
        if
            not (DatabaseRestoreCanonical.exactProperties names root)
            || text "format" root <> "claimcore-restore-evidence-index-1"
        then
            invalidOp "Restore evidence index format is unsupported."

        {
            InstallationId = exactId "installationId" root
            LineageId = exactId "lineageId" root
            Epoch = DatabaseRestoreCanonical.number "epoch" root
            CycleId = exactId "cycleId" root
            BackupCaptureSequence = DatabaseRestoreCanonical.number "backupCaptureSequence" root
            BackupCaptureHash = sha "backupCaptureHash" root
            PublicationManifestSha256 = sha "publicationManifestSha256" root
            ArchiveRoot = path "archiveRoot" root
            ArchiveSetId = exactId "archiveSetId" root
            ArchiveObjects =
                DatabaseRestoreArchiveObjects.parse (root.GetProperty("archiveObjects"))
            PrimaryCaptureWalEndpoint = wal "primaryCaptureWalEndpoint" root
            WitnessCaptureWalEndpoint = wal "witnessCaptureWalEndpoint" root
            PrimaryRegisteredWalHorizon = wal "primaryRegisteredWalHorizon" root
            WitnessRegisteredWalHorizon = wal "witnessRegisteredWalHorizon" root
            PrimaryWalEndpoint = wal "primaryWalEndpoint" root
            WitnessWalEndpoint = wal "witnessWalEndpoint" root
            CheckpointRoot = path "checkpointRoot" root
            CheckpointFile = path "checkpointFile" root
            CheckpointSignatureFile = path "checkpointSignatureFile" root
            ManifestFile = path "manifestFile" root
            ManifestSignatureFile = path "manifestSignatureFile" root
            ManifestSha256 = sha "manifestSha256" root
            BarrierFile = path "barrierFile" root
            BarrierSignatureFile = path "barrierSignatureFile" root
            InventoryRoot = path "inventoryRoot" root
            InventorySnapshotFile = path "inventorySnapshotFile" root
            InventorySnapshotSignatureFile = path "inventorySnapshotSignatureFile" root
            CheckpointSignerKeyId = exactId "checkpointSignerKeyId" root
            CheckpointObjectId = exactId "checkpointObjectId" root
        }

    let private identityMatches root (report: RestoreReportClaims) (result: RestoreEvidenceIndex) =
        result.InstallationId = report.InstallationId
        && result.LineageId = report.LineageId
        && result.Epoch = report.Epoch
        && result.CycleId = report.CycleId
        && result.BackupCaptureSequence = report.BackupCaptureSequence
        && result.BackupCaptureHash = report.BackupCaptureHash
        && result.PrimaryRegisteredWalHorizon = report.PrimaryRegisteredWalHorizon
        && result.WitnessRegisteredWalHorizon = report.WitnessRegisteredWalHorizon
        && result.PublicationManifestSha256 <> String.replicate 64 "0"
        && text "primarySystemId" root = report.PrimarySystemId
        && text "witnessSystemId" root = report.WitnessSystemId
        && DatabaseRestoreCanonical.number "primaryTimeline" root = report.PrimaryTimeline
        && DatabaseRestoreCanonical.number "witnessTimeline" root = report.WitnessTimeline

    let private custodyMatches root (report: RestoreReportClaims) (result: RestoreEvidenceIndex) =
        exactId "reportSignerKeyId" root = report.SignerKeyId
        && result.CheckpointSignerKeyId <> report.SignerKeyId
        && result.CheckpointSignerKeyId = report.CustodyKeyId
        && result.ArchiveSetId = report.ArchiveCustody.ObjectId
        && DatabaseRestoreArchiveObjects.rootDigest result.ArchiveObjects =
            report.ArchiveCustody.Sha256
        && DatabaseRestoreArchiveObjects.totalBytes result.ArchiveObjects =
            report.ArchiveCustody.Bytes
        && result.CheckpointObjectId = report.CheckpointCustody.ObjectId

    let private rootsSeparate (result: RestoreEvidenceIndex) =
        let overlaps (left: string) (right: string) =
            left = right
            || left.StartsWith(right + "/", StringComparison.Ordinal)
            || right.StartsWith(left + "/", StringComparison.Ordinal)

        not (
            overlaps result.ArchiveRoot result.CheckpointRoot
            || overlaps result.ArchiveRoot result.InventoryRoot
            || overlaps result.CheckpointRoot result.InventoryRoot
        )

    let private parsed root (report: RestoreReportClaims) =
        let result: RestoreEvidenceIndex = project root

        if
            not (
                identityMatches root report result
                && custodyMatches root report result
                && rootsSeparate result
            )
        then
            invalidOp "Restore evidence index disagrees with signed report."

        result

    let parse (bytes: byte array) report =
        match DatabaseRestoreCanonical.parse bytes with
        | None -> None
        | Some document ->
            use document = document

            try
                Some(parsed document.RootElement report)
            with _ ->
                None
