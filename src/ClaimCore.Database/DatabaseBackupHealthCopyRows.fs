namespace ClaimCore.Database

open System
open Npgsql
open NpgsqlTypes
open ClaimCore.Postgres

[<NoEquality; NoComparison>]
type private HealthCopyRow =
    {
        Producer: string
        Cluster: string
        Kind: string
        State: string
        Revision: int64
        SystemId: string
        Timeline: int64
        SegmentBytes: int
        BaseWalEnd: string option
        Segment: string option
        CiphertextSha256: string
        CiphertextBytes: int64
        VerifiedAt: DateTimeOffset
        PhysicalReceiptSha256: string
        RetainUntil: DateTimeOffset
        CapturedAt: DateTimeOffset
        WitnessCutoff: int64
        Registration: ManagedCopyAttestation
    }

module internal DatabaseBackupHealthCopyRows =
    let private current (owner: NpgsqlConnection) (transaction: NpgsqlTransaction) copyId =
        use command =
            new NpgsqlCommand(
                "SELECT c.producer_kind,c.cluster_name,c.copy_kind,c.state,c.revision,"
                + "c.postgres_system_id,c.timeline,c.wal_segment_bytes,c.wal_end_lsn,"
                + "c.wal_segment,c.ciphertext_sha256,c.ciphertext_bytes,c.last_verified_at,"
                + "c.verification_proof_sha256,c.retain_until,c.captured_at,"
                + "c.witness_cutoff_sequence,e.canonical_attestation "
                + "FROM claimcore.managed_copies c "
                + "JOIN claimcore.managed_copy_events e ON e.copy_id=c.copy_id "
                + "AND e.revision=1 WHERE c.copy_id=@copy",
                owner,
                transaction
            )

        command.Parameters.AddWithValue("copy", NpgsqlDbType.Uuid, copyId) |> ignore
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Backup health managed copy is absent."

        let optional index =
            if reader.IsDBNull(index) then
                None
            else
                Some(reader.GetString(index))

        let row =
            {
                Producer = reader.GetString(0)
                Cluster = reader.GetString(1)
                Kind = reader.GetString(2)
                State = reader.GetString(3)
                Revision = reader.GetInt64(4)
                SystemId = reader.GetString(5)
                Timeline = int64 (reader.GetInt32(6))
                SegmentBytes = reader.GetInt32(7)
                BaseWalEnd = optional 8
                Segment = optional 9
                CiphertextSha256 = reader.GetFieldValue<byte array>(10) |> Convert.ToHexStringLower
                CiphertextBytes = reader.GetInt64(11)
                VerifiedAt = reader.GetFieldValue<DateTimeOffset>(12)
                PhysicalReceiptSha256 =
                    reader.GetFieldValue<byte array>(13) |> Convert.ToHexStringLower
                RetainUntil = reader.GetFieldValue<DateTimeOffset>(14)
                CapturedAt = reader.GetFieldValue<DateTimeOffset>(15)
                WitnessCutoff = reader.GetInt64(16)
                Registration =
                    reader.GetFieldValue<byte array>(17)
                    |> ManagedCopyRegistrationAttestation.parse
                    |> Option.defaultWith (fun () ->
                        invalidOp "Backup health copy registration is invalid.")
            }

        if reader.Read() then
            invalidOp "Backup health managed copy is ambiguous."

        row

    let private matchingIdentity
        (item: BackupHealthArchiveObject)
        (evidence: BackupHealthEvidence)
        (row: HealthCopyRow)
        =
        row.Producer = "OWNER_ATTESTED"
        && row.Cluster = item.Cluster
        && row.Kind = item.Kind
        && row.State = "RETAINED"
        && row.Revision = item.Revision
        && row.SystemId = item.PostgresSystemId
        && row.Timeline = item.Timeline
        && row.SegmentBytes = item.WalSegmentBytes
        && row.CiphertextSha256 = item.CiphertextSha256
        && row.CiphertextBytes = item.CiphertextBytes
        && row.VerifiedAt = item.VerifiedAt
        && row.PhysicalReceiptSha256 = item.PhysicalReceiptSha256
        && row.Registration.CopyId = item.CopyId
        && row.Registration.InstallationId = evidence.InstallationId
        && row.Registration.LineageId = evidence.LineageId
        && row.Registration.Epoch = evidence.Epoch
        && row.Registration.WitnessCutoffSequence <= evidence.WitnessTipSequence
        && (item.Kind <> "BASE"
            || (row.Registration.CycleId = Some evidence.CycleId
                && row.Registration.WitnessCutoffSequence = evidence.BackupCaptureSequence
                && Convert.ToHexStringLower(row.Registration.WitnessCutoffHash) =
                    evidence.BackupCaptureHash))
        && ((item.Kind = "BASE"
             && row.BaseWalEnd = Some item.WalHorizon
             && row.Segment.IsNone)
            || (item.Kind = "WAL" && row.BaseWalEnd.IsNone && row.Segment = item.WalSegment))

    let private matchingHorizon
        (item: BackupHealthArchiveObject)
        (policy: BackupHealthPolicy)
        now
        (row: HealthCopyRow)
        =
        let maximum =
            if item.Kind = "BASE" then
                policy.MaximumBackupAgeSeconds
            else
                policy.RestoreHorizonSeconds

        row.CapturedAt <= now
        && row.VerifiedAt <= now
        && now - row.CapturedAt <= TimeSpan.FromSeconds(float maximum)
        && now - row.VerifiedAt <= TimeSpan.FromSeconds(float maximum)
        && row.RetainUntil >= now.AddSeconds(float policy.RestoreHorizonSeconds)
        && row.WitnessCutoff >= 0L

    let verify
        (owner: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (policy: BackupHealthPolicy)
        (evidence: BackupHealthEvidence)
        now
        =
        for item in evidence.Objects do
            let row = current owner transaction item.CopyId

            if not (matchingIdentity item evidence row && matchingHorizon item policy now row) then
                invalidOp "Backup health managed copy is not current, retained or physical."
