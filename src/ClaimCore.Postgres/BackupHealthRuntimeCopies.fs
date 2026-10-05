namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open NpgsqlTypes

[<NoEquality; NoComparison>]
type internal BackupHealthRuntimeCopy =
    {
        Id: Guid
        Cluster: string
        Kind: string
        State: string
        Revision: int64
        SystemId: string
        Timeline: int64
        SegmentBytes: int
        BaseEnd: string option
        Segment: string option
        PhysicalReceiptSha256: string option
        CapturedAt: DateTimeOffset
        VerifiedAt: DateTimeOffset option
        RetainUntil: DateTimeOffset
    }

module internal BackupHealthRuntimeCopies =
    let private decode (reader: System.Data.Common.DbDataReader) =
        let optionalText index =
            if reader.IsDBNull(index) then
                None
            else
                Some(reader.GetString(index))

        {
            Id = reader.GetGuid(0)
            Cluster = reader.GetString(1)
            Kind = reader.GetString(2)
            State = reader.GetString(3)
            Revision = reader.GetInt64(4)
            SystemId = reader.GetString(5)
            Timeline = int64 (reader.GetInt32(6))
            SegmentBytes = reader.GetInt32(7)
            BaseEnd = optionalText 8
            Segment = optionalText 9
            PhysicalReceiptSha256 =
                if reader.IsDBNull(10) then
                    None
                else
                    Some(reader.GetFieldValue<byte array>(10) |> Convert.ToHexStringLower)
            CapturedAt = reader.GetFieldValue<DateTimeOffset>(11)
            VerifiedAt =
                if reader.IsDBNull(12) then
                    None
                else
                    Some(reader.GetFieldValue<DateTimeOffset>(12))
            RetainUntil = reader.GetFieldValue<DateTimeOffset>(13)
        }

    let private read
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        ids
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT copy_id,cluster_name,copy_kind,state,revision,postgres_system_id,"
                    + "timeline,wal_segment_bytes,wal_end_lsn,wal_segment,"
                    + "verification_proof_sha256,captured_at,last_verified_at,retain_until "
                    + "FROM claimcore.managed_copies WHERE copy_id=ANY(@ids) "
                    + "AND producer_kind='OWNER_ATTESTED'",
                    connection,
                    transaction
                )

            command.Parameters.AddWithValue("ids", NpgsqlDbType.Array ||| NpgsqlDbType.Uuid, ids)
            |> ignore

            use! reader = command.ExecuteReaderAsync(ct)
            let rows = ResizeArray<BackupHealthRuntimeCopy>()

            while! reader.ReadAsync(ct) do
                rows.Add(decode reader)



            return rows |> Seq.map (fun row -> row.Id, row) |> Map.ofSeq
        }

    let private current (claims: BackupHealthClaims) now (row: BackupHealthRuntimeCopy) =
        row.State = "RETAINED"
        && row.PhysicalReceiptSha256.IsSome
        && row.VerifiedAt.IsSome
        && row.CapturedAt <= now
        && row.RetainUntil >= now.AddSeconds(float claims.RestoreHorizonSeconds)

    let private baseCopy
        (claims: BackupHealthClaims)
        (baseClaim: BackupHealthBase)
        cluster
        system
        timeline
        now
        (row: BackupHealthRuntimeCopy)
        =
        current claims now row
        && row.Cluster = cluster
        && row.Kind = "BASE"
        && row.Revision = baseClaim.Revision
        && row.SystemId = system
        && row.Timeline = timeline
        && row.BaseEnd.IsSome
        && row.Segment.IsNone
        && row.PhysicalReceiptSha256 = Some baseClaim.PhysicalReceiptSha256
        && row.VerifiedAt = Some baseClaim.VerifiedAt
        && now - row.CapturedAt
           <= TimeSpan.FromSeconds(float claims.MaximumBackupAgeSeconds)

    let private wal claim cluster system timeline now (row: BackupHealthRuntimeCopy) =
        current claim now row
        && row.Cluster = cluster
        && row.Kind = "WAL"
        && row.SystemId = system
        && row.Timeline = timeline
        && row.BaseEnd.IsNone
        && row.Segment.IsSome
        && now - row.CapturedAt <= TimeSpan.FromSeconds(float claim.RestoreHorizonSeconds)

    let private invalidCopies (claims: BackupHealthClaims) now get =
        [
            "PRIMARY",
            claims.PrimarySystemId,
            claims.PrimaryTimeline,
            claims.PrimaryBase,
            claims.PrimaryWal.CopyIds
            "WITNESS",
            claims.WitnessSystemId,
            claims.WitnessTimeline,
            claims.WitnessBase,
            claims.WitnessWal.CopyIds
        ]
        |> List.exists (fun (cluster, system, timeline, baseline, walIds) ->
            not (baseCopy claims baseline cluster system timeline now (get baseline.CopyId))
            || walIds
               |> List.exists (fun id -> not (wal claims cluster system timeline now (get id))))

    let verify
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (claims: BackupHealthClaims)
        now
        (ct: CancellationToken)
        =
        task {
            let ids =
                [ claims.PrimaryBase.CopyId; claims.WitnessBase.CopyId ]
                @ claims.PrimaryWal.CopyIds
                @ claims.WitnessWal.CopyIds

            let unique = ids |> List.distinct

            if unique.Length <> ids.Length then
                invalidOp "Backup health copy IDs overlap."

            let! rows = read connection transaction (List.toArray unique) ct

            if rows.Count <> ids.Length then
                invalidOp "Backup health copy inventory is incomplete."

            let get id = rows[id]

            if invalidCopies claims now get then
                invalidOp "Backup health copy rows are not current and retained."

            return rows
        }
