namespace ClaimCore.Postgres

open System
open System.Data.Common
open System.Threading
open Npgsql
open NpgsqlTypes
open ClaimCore.Domain

type internal LifecycleAuditHoldRow =
    {
        Hold: LifecycleHold
        ReleasedBy: Guid option
        ReleasedAt: DateTimeOffset option
        ReleaseReason: string option
    }

module internal CaseLifecycleAuditRelations =
    let private optional (reader: DbDataReader) index get =
        if reader.IsDBNull(index) then
            None
        else
            Some(get reader index)

    let private holdRow (reader: DbDataReader) =
        {
            Hold =
                {
                    Id = reader.GetGuid(0)
                    Ground = reader.GetString(1)
                    ReviewOn = reader.GetFieldValue<DateOnly>(2)
                    RecordedBy = reader.GetGuid(3)
                    RecordedAt = reader.GetFieldValue<DateTimeOffset>(4)
                }
            ReleasedBy = optional reader 5 (fun value index -> value.GetGuid(index))
            ReleasedAt =
                optional reader 6 (fun value index -> value.GetFieldValue<DateTimeOffset>(index))
            ReleaseReason = optional reader 7 (fun value index -> value.GetString(index))
        }

    let holdById connection transaction caseId holdId (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT hold_id,ground,review_on,recorded_by,recorded_at,"
                    + "released_by,released_at,release_reason FROM claimcore.case_holds "
                    + "WHERE case_id=@caseId AND hold_id=@hold",
                    connection,
                    transaction
                )

            command.Parameters.AddWithValue("hold", NpgsqlDbType.Uuid, holdId) |> ignore
            command.Parameters.AddWithValue("caseId", NpgsqlDbType.Uuid, caseId) |> ignore
            let! result = command.ExecuteReaderAsync(ct)
            use reader = result
            let! found = reader.ReadAsync(ct)
            return if found then Some(holdRow reader) else None
        }

    let holdCount connection transaction caseId (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT count(*) FROM claimcore.case_holds WHERE case_id=@caseId",
                    connection,
                    transaction
                )

            command.Parameters.AddWithValue("caseId", NpgsqlDbType.Uuid, caseId) |> ignore
            let! value = command.ExecuteScalarAsync(ct)
            return value :?> int64
        }

    let activeHoldCount connection transaction caseId (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT count(*) FROM claimcore.case_holds "
                    + "WHERE case_id=@caseId AND released_at IS NULL",
                    connection,
                    transaction
                )

            command.Parameters.AddWithValue("caseId", NpgsqlDbType.Uuid, caseId) |> ignore
            let! value = command.ExecuteScalarAsync(ct)
            return value :?> int64
        }

    let businessPage connection transaction caseId afterRevision limit (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT revision,snapshot FROM claimcore.case_changes "
                    + "WHERE case_id=@caseId AND revision>@after ORDER BY revision LIMIT @limit",
                    connection,
                    transaction
                )

            command.Parameters.AddWithValue("caseId", NpgsqlDbType.Uuid, caseId) |> ignore

            command.Parameters.AddWithValue("after", NpgsqlDbType.Bigint, afterRevision)
            |> ignore

            command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit) |> ignore
            let! result = command.ExecuteReaderAsync(ct)
            use reader = result
            let rows = ResizeArray<int64 * byte array>()
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync(ct)
                reading <- found

                if found then
                    rows.Add(reader.GetInt64(0), reader.GetFieldValue<byte array>(1))

            return List.ofSeq rows
        }
