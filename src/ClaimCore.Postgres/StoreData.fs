namespace ClaimCore.Postgres

open System
open System.IO
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Witness

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

    let read (dataSource: NpgsqlDataSource) cancellationToken action =
        task {
            try
                use! connection =
                    RuntimeDatabase.openConnectionAsyncWithCancellation dataSource cancellationToken

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

    let readAcceptedUnderLock
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        operationId
        requestSha256
        =
        task {
            use command = new NpgsqlCommand(Sql.operation, connection, transaction)

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

    /// Called only after the case lock. A fresh OPEN uses its retained reserved ID and never
    /// re-mints it. A losing concurrent OPEN may observe another accepted ID so Domain can
    /// issue its normal version refusal; that observed ID is never persisted by this attempt.
    let caseIdForDecision
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        reference
        (current: Claim option)
        expectedCaseId
        allowCompetingOpen
        =
        task {
            match current, expectedCaseId with
            | None, Some caseId when caseId <> Guid.Empty -> return caseId
            | None, _ ->
                return raise (InvalidDataException("A fresh case lacks a reserved identity."))
            | Some _, _ ->
                use command =
                    new NpgsqlCommand(
                        "SELECT case_id FROM claimcore.cases WHERE case_reference = @reference FOR UPDATE",
                        connection,
                        transaction
                    )

                Sql.text command "reference" reference
                let! value = command.ExecuteScalarAsync()

                return
                    match value with
                    | :? Guid as caseId when
                        caseId <> Guid.Empty && (expectedCaseId = Some caseId || allowCompetingOpen)
                        ->
                        caseId
                    | _ -> raise (InvalidDataException("The current case identity is absent."))
        }

    let private persistCurrent
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        request
        caseId
        claim
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
            Sql.uuid command "caseId" caseId
            Sql.integer command "revision" snapshot.Version

            if snapshot.Version > 1L then
                Sql.integer command "expected" request.ExpectedVersion

            let! affected = command.ExecuteNonQueryAsync()

            if affected <> 1 then
                raise (
                    InvalidDataException("Conditional persistence did not affect exactly one case.")
                )
        }

    let private bindAcceptedEvidence (audit: NpgsqlCommand) operation (context: BusinessContext) =
        Sql.text audit "fingerprint" (Operation.fingerprint operation)

        Sql.add
            audit
            "canonicalRequest"
            NpgsqlDbType.Bytea
            (box (Operation.canonicalRequest operation))

        Sql.add
            audit
            "effectiveBusinessDate"
            NpgsqlDbType.Integer
            (box (ScalarEncoding.dateDays (context.EffectiveBusinessDate.ToString("yyyy-MM-dd"))))

        Sql.add audit "observedUtcInstant" NpgsqlDbType.TimestampTz (box context.ObservedUtcInstant)

    let private bindAttribution (audit: NpgsqlCommand) caseId (attribution: ExecutionAttribution) =
        if attribution.Command.CaseId <> caseId then
            raise (InvalidDataException("Accepted actor evidence names a different case."))

        Sql.uuid audit "preparer" attribution.PreparerActorId
        Sql.optional audit "importer" NpgsqlDbType.Uuid attribution.ImporterActorId

        let submitter, resolver =
            match attribution.Phase with
            | AttemptActorPhase.NormalSubmit -> Some attribution.Command.Actor.ActorId, None
            | AttemptActorPhase.RecoveryResolve -> None, Some attribution.Command.Actor.ActorId

        Sql.optional audit "submitter" NpgsqlDbType.Uuid submitter
        Sql.optional audit "resolver" NpgsqlDbType.Uuid resolver
        Sql.uuid audit "acceptedActor" attribution.Command.Actor.ActorId
        Sql.integer audit "grantRevision" attribution.Command.Actor.GrantRevision

    let private persistAccepted
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        operation
        (context: BusinessContext)
        caseId
        (attribution: ExecutionAttribution)
        claim
        (ticket: Ticket)
        =
        task {
            let request = Operation.request operation
            let snapshot = Claim.view claim
            use audit = new NpgsqlCommand(Sql.insertChange, connection, transaction)
            Sql.uuid audit "operation" request.OperationId
            Sql.uuid audit "caseId" caseId
            Sql.text audit "reference" snapshot.Fields.CaseReference

            bindAttribution audit caseId attribution
            Sql.integer audit "revision" snapshot.Version
            Sql.text audit "command" (Commands.name request.Command)
            bindAcceptedEvidence audit operation context

            Sql.add audit "snapshot" NpgsqlDbType.Bytea (box (CaseRecord.encodeSnapshot snapshot))
            Sql.integer audit "witnessSequence" ticket.Sequence
            Sql.integer audit "witnessEpoch" ticket.Epoch
            Sql.add audit "witnessHash" NpgsqlDbType.Bytea (box ticket.EntryHash)

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

    let persist connection transaction operation context caseId attribution claim ticket =
        task {
            do! persistCurrent connection transaction (Operation.request operation) caseId claim

            return!
                persistAccepted
                    connection
                    transaction
                    operation
                    context
                    caseId
                    attribution
                    claim
                    ticket
        }
