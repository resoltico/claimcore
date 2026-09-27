namespace ClaimCore.Database

open System
open System.Text.Json
open System.Text.RegularExpressions
open ClaimCore.Postgres

module internal DatabaseBackupHealthEvidenceParts =
    open BackupHealthFields

    let private require fields (value: JsonElement) =
        if not (BackupHealthCanonical.exact value fields) then
            invalidOp "Independent backup health evidence is not exact."

    let relative (value: JsonElement) name =
        let path = text value name
        let segments = path.Split('/')

        if
            segments.Length < 1
            || segments.Length > 4
            || not (Regex.IsMatch(path, "^[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+){0,3}$"))
            || segments |> Array.exists (fun item -> item = "." || item = "..")
        then
            invalidOp "Independent backup health object path is invalid."

        path

    let private requireArchiveIdentity
        (cluster: string)
        (kind: string)
        (revision: int64)
        (length: int64)
        (segmentBytes: int64)
        (segment: string option)
        =
        if
            (cluster <> "PRIMARY" && cluster <> "WITNESS")
            || (kind <> "BASE" && kind <> "WAL")
            || revision < 2L
            || length < 1L
            || length > 1099511627776L
            || segmentBytes < 1048576L
            || segmentBytes > 1073741824L
            || segmentBytes &&& (segmentBytes - 1L) <> 0L
            || (kind = "BASE" && segment.IsSome)
            || (kind = "WAL"
                && (segment.IsNone || not (Regex.IsMatch(segment.Value, "^[0-9A-F]{24}$"))))
        then
            invalidOp "Independent backup health copy identity is invalid."

    let archiveObject (value: JsonElement) =
        require
            [
                "copyId"
                "revision"
                "cluster"
                "kind"
                "postgresSystemId"
                "timeline"
                "walHorizon"
                "ciphertextSha256"
                "ciphertextBytes"
                "walSegmentBytes"
                "walSegment"
                "physicalReceiptSha256"
                "relativePath"
                "verifiedAt"
            ]
            value

        let cluster = text value "cluster"
        let kind = text value "kind"
        let revision = number value "revision"
        let length = number value "ciphertextBytes"
        let segment = optionalText value "walSegment"
        let segmentBytes = number value "walSegmentBytes"

        requireArchiveIdentity cluster kind revision length segmentBytes segment

        {
            CopyId = uuid value "copyId"
            Revision = revision
            Cluster = cluster
            Kind = kind
            PostgresSystemId = systemId value "postgresSystemId"
            Timeline = positive (int64 Int32.MaxValue) (number value "timeline")
            WalSegmentBytes = int segmentBytes
            WalHorizon = lsn value "walHorizon"
            WalSegment = segment
            CiphertextSha256 = sha value "ciphertextSha256"
            CiphertextBytes = length
            PhysicalReceiptSha256 = sha value "physicalReceiptSha256"
            RelativePath = relative value "relativePath"
            VerifiedAt = instant value "verifiedAt"
        }

    let checkpoint (value: JsonElement) =
        require
            [
                "sequence"
                "hash"
                "objectSha256"
                "objectBytes"
                "relativePath"
                "verifiedAt"
            ]
            value

        let sequence = number value "sequence"
        let length = number value "objectBytes"

        if sequence < 0L || length < 1L || length > 16777216L then
            invalidOp "Independent checkpoint object is invalid."

        {
            Sequence = sequence
            Hash = sha value "hash"
            ObjectSha256 = sha value "objectSha256"
            ObjectBytes = length
            RelativePath = relative value "relativePath"
            VerifiedAt = instant value "verifiedAt"
        }

    let restored (value: JsonElement) =
        require
            [
                "reportSha256"
                "reportRelativePath"
                "fullAuditSha256"
                "auditRelativePath"
                "witnessCutoff"
                "witnessCutoffHash"
                "primaryBaseCopyId"
                "witnessBaseCopyId"
                "primaryWalHorizon"
                "witnessWalHorizon"
                "primarySystemId"
                "primaryTimeline"
                "witnessSystemId"
                "witnessTimeline"
                "verifiedAt"
            ]
            value

        let cutoff = number value "witnessCutoff"

        if cutoff < 0L then
            invalidOp "Independent restored-pair cutoff is invalid."

        {
            ReportSha256 = sha value "reportSha256"
            ReportRelativePath = relative value "reportRelativePath"
            FullAuditSha256 = sha value "fullAuditSha256"
            AuditRelativePath = relative value "auditRelativePath"
            WitnessCutoff = cutoff
            WitnessCutoffHash = sha value "witnessCutoffHash"
            PrimaryBaseCopyId = uuid value "primaryBaseCopyId"
            WitnessBaseCopyId = uuid value "witnessBaseCopyId"
            PrimaryWalHorizon = lsn value "primaryWalHorizon"
            WitnessWalHorizon = lsn value "witnessWalHorizon"
            PrimarySystemId = systemId value "primarySystemId"
            PrimaryTimeline = positive (int64 Int32.MaxValue) (number value "primaryTimeline")
            WitnessSystemId = systemId value "witnessSystemId"
            WitnessTimeline = positive (int64 Int32.MaxValue) (number value "witnessTimeline")
            VerifiedAt = instant value "verifiedAt"
        }
