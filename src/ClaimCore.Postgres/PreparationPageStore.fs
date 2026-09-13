namespace ClaimCore.Postgres

open System.Data.Common
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open PreparationData

/// Reads paged technical recovery material without admitting any lifecycle transition.
module internal PreparationPageStore =
    let private createCommand (connection: NpgsqlConnection) =
        new NpgsqlCommand(
            select
            + " WHERE (@afterPreparedAt IS NULL OR p.prepared_at < @afterPreparedAt "
            + "OR (p.prepared_at = @afterPreparedAt AND p.operation_id < @afterOperation)) "
            + "ORDER BY p.prepared_at DESC, p.operation_id DESC LIMIT @limit",
            connection
        )

    let private addCursor command after pageSize =
        Sql.optional
            command
            "afterPreparedAt"
            NpgsqlDbType.TimestampTz
            (after |> Option.map (fun (cursor: RecoveryCursor) -> cursor.PreparedAt))

        Sql.optional
            command
            "afterOperation"
            NpgsqlDbType.Uuid
            (after |> Option.map (fun (cursor: RecoveryCursor) -> cursor.OperationId))

        let limit = command.Parameters.Add("limit", NpgsqlDbType.Integer)
        limit.Value <- pageSize + 1

    let private readPreparations (reader: DbDataReader) =
        task {
            let found = ResizeArray<RetainedPreparation>()
            let mutable hasRow = true

            while hasRow do
                let! row = reader.ReadAsync()
                hasRow <- row

                if row then
                    found.Add(read reader)

            return found
        }

    let private toPage pageSize (found: ResizeArray<RetainedPreparation>) =
        let items = found |> Seq.truncate pageSize |> Seq.toList

        {
            Items = items
            NextAfter =
                if found.Count > pageSize then
                    items
                    |> List.tryLast
                    |> Option.map (fun item ->
                        {
                            PreparedAt = item.PreparedAt
                            OperationId = item.OperationId
                        })
                else
                    None
        }

    let list (connection: NpgsqlConnection) after pageSize : Task<RecoveryStorePage> =
        task {
            use command = createCommand connection
            addCursor command after pageSize
            let! dataReader = command.ExecuteReaderAsync()
            use reader = dataReader
            let! found = readPreparations reader
            return toPage pageSize found
        }
