namespace ClaimCore.Postgres

open System
open System.IO
open System.Text
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat

module internal StoreData =
    let failure committing operationId (exceptionValue: exn) =
        if committing then
            CoreFailure.CommitOutcomeUnknown operationId
        else
            match exceptionValue with
            | UnsupportedPostgresVersion
            | RuntimeDatabaseMismatch -> CoreFailure.SchemaMismatch
            | :? InvalidDataException -> CoreFailure.StoreCorrupt
            | :? InvalidCastException -> CoreFailure.StoreCorrupt
            | :? PostgresException as error when
                error.SqlState = "42P01" || error.SqlState = "3F000"
                ->
                CoreFailure.SchemaMismatch
            | _ -> CoreFailure.StoreUnavailable

    let read (dataSource: NpgsqlDataSource) action =
        task {
            try
                use! connection = RuntimeDatabase.openConnectionAsync dataSource
                let! result = action connection
                return Ok result
            with
            | UnsupportedPostgresVersion
            | RuntimeDatabaseMismatch -> return Error CoreFailure.SchemaMismatch
            | :? NpgsqlException as error -> return Error(failure false Guid.Empty error)
            | :? TimeoutException as error -> return Error(failure false Guid.Empty error)
            | :? InvalidDataException as error -> return Error(failure false Guid.Empty error)
            | :? InvalidCastException as error -> return Error(failure false Guid.Empty error)
        }

    let readOperation
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction option)
        operationId
        =
        task {
            use command =
                match transaction with
                | Some value -> new NpgsqlCommand(Sql.operation, connection, value)
                | None -> new NpgsqlCommand(Sql.operation, connection)

            Sql.uuid command "operation" operationId
            let! result = command.ExecuteReaderAsync()
            use reader = result
            let! exists = reader.ReadAsync()

            return
                if exists then
                    Some(
                        Rows.receipt reader true,
                        reader.GetString(reader.GetOrdinal("request_sha256"))
                    )
                else
                    None
        }

    let readAccepted (connection: NpgsqlConnection) operationId requestSha256 =
        task {
            use command = new NpgsqlCommand(Sql.operation, connection)
            Sql.uuid command "operation" operationId
            let! result = command.ExecuteReaderAsync()
            use reader = result
            let! exists = reader.ReadAsync()

            if not exists then
                return Ok None
            elif reader.GetString(reader.GetOrdinal("request_sha256")) <> requestSha256 then
                return Error CoreFailure.IdempotencyConflict
            else
                return Ok(Some(Rows.receipt reader true))
        }

    let readCase (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) reference =
        task {
            use command =
                new NpgsqlCommand(Sql.selectCase + " FOR UPDATE OF c", connection, transaction)

            Sql.text command "reference" reference
            let! result = command.ExecuteReaderAsync()
            use reader = result
            let! exists = reader.ReadAsync()
            return if exists then Some(Rows.claim reader) else None
        }

    let persist
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (request: CommandRequest)
        claim
        fingerprint
        =
        task {
            let snapshot = Claim.view claim

            let sql =
                if snapshot.Version = 1L then
                    Sql.insertCase
                else
                    Sql.updateCase

            use command = new NpgsqlCommand(sql, connection, transaction)
            Rows.bindClaim command claim
            Sql.integer command "revision" snapshot.Version

            if snapshot.Version > 1L then
                Sql.integer command "expected" request.ExpectedVersion

            let! affected = command.ExecuteNonQueryAsync()

            if affected <> 1 then
                raise (
                    InvalidDataException("Conditional persistence did not affect exactly one case.")
                )

            use audit = new NpgsqlCommand(Sql.insertChange, connection, transaction)
            Sql.uuid audit "operation" request.OperationId
            Sql.text audit "reference" snapshot.Fields.CaseReference
            Sql.integer audit "revision" snapshot.Version
            Sql.text audit "command" (Commands.name request.Command)
            Sql.text audit "fingerprint" fingerprint

            Sql.add
                audit
                "snapshot"
                NpgsqlDbType.Jsonb
                (box (snapshot |> CaseRecord.encodeSnapshot |> Encoding.UTF8.GetString))

            let! result = audit.ExecuteReaderAsync()
            use reader = result
            let! exists = reader.ReadAsync()

            if not exists then
                raise (InvalidDataException("The audit row was not returned."))

            return
                {
                    OperationId = request.OperationId
                    Case = claim
                    RecordedAt = reader.GetFieldValue<DateTimeOffset>(0)
                    RecordedBy = reader.GetString(1)
                    Replayed = false
                    CommandName = Commands.name request.Command
                }
        }
