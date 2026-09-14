namespace ClaimCore.Postgres

open System
open System.Data
open System.Data.Common
open System.IO
open System.Threading
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open ClaimCore.RecordFormat

module internal PreparationData =
    let select =
        """
        SELECT p.operation_id, p.canonical_request_format, p.request_sha256, p.canonical_request,
            p.prepared_at, p.prepared_application_version, p.preparing_contract_fingerprint,
            p.preparing_contract_kind,
            l.state, l.recorded_at
        FROM claimcore.request_preparations p
        LEFT JOIN claimcore.request_preparation_lifecycle l ON l.operation_id = p.operation_id
        """

    let fail error =
        match error with
        | UnsupportedPostgresVersion
        | RuntimeDatabaseMismatch -> RecoveryStoreFailure.SchemaMismatch
        | :? InvalidDataException
        | :? InvalidCastException -> RecoveryStoreFailure.StoreCorrupt
        | :? PostgresException as postgresql when
            postgresql.SqlState = "42P01" || postgresql.SqlState = "3F000"
            ->
            RecoveryStoreFailure.SchemaMismatch
        | _ -> RecoveryStoreFailure.StoreUnavailable

    let mutationFailure commitStarted (error: exn) =
        if commitStarted then
            RecoveryStoreFailure.TechnicalMutationUnknown
        else
            match error with
            | :? OperationCanceledException -> RecoveryStoreFailure.CancelledBeforeCommit
            | _ -> fail error

    let private lifecycle (reader: DbDataReader) =
        if reader.IsDBNull(8) then
            PreparationLifecycle.Unsubmitted
        else
            let recordedAt = reader.GetFieldValue<DateTimeOffset>(9)

            match reader.GetString(8) with
            | "SUBMISSION_STARTED" -> PreparationLifecycle.SubmissionStarted recordedAt
            | "DISMISSED" -> PreparationLifecycle.Dismissed recordedAt
            | _ -> raise (InvalidDataException("Stored preparation lifecycle is unknown."))

    let read (reader: DbDataReader) : RetainedPreparation =
        if reader.GetInt16(1) <> int16 RecordVersions.CanonicalCommandFormat then
            raise (InvalidDataException("Stored canonical request format is unsupported."))

        let kind =
            reader.GetString(7)
            |> PreparingContractKindEncoding.parse
            |> Option.defaultWith (fun () ->
                raise (InvalidDataException("Stored preparation contract provenance is unknown.")))

        let value =
            {
                OperationId = reader.GetGuid(0)
                CanonicalRequestFormat = reader.GetInt16(1) |> int
                RequestSha256 = reader.GetString(2)
                CanonicalRequest = reader.GetFieldValue<byte array>(3) |> Array.copy
                PreparedAt = reader.GetFieldValue<DateTimeOffset>(4)
                PreparingApplicationVersion = reader.GetString(5)
                PreparingContractFingerprint = reader.GetString(6)
                PreparingContractKind = kind
                Lifecycle = lifecycle reader
            }

        match PreparationIntegrity.verify value with
        | Ok _ -> value
        | Error _ -> raise (InvalidDataException("Stored preparation failed integrity checks."))

    let commitBoundary
        (cancellationToken: CancellationToken)
        (commitStarted: bool ref)
        (commit: unit -> Task)
        : Task =
        task {
            cancellationToken.ThrowIfCancellationRequested()
            commitStarted.Value <- true
            do! commit ()
        }

    let withTransaction
        (connection: NpgsqlConnection)
        (cancellationToken: CancellationToken)
        (commitStarted: bool ref)
        (action: NpgsqlTransaction -> Task<'result>)
        : Task<'result> =
        task {
            cancellationToken.ThrowIfCancellationRequested()

            let! transaction =
                connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)

            use _ = transaction

            try
                let! result = action transaction

                do!
                    commitBoundary cancellationToken commitStarted (fun () ->
                        transaction.CommitAsync(CancellationToken.None))

                return result
            with error ->
                try
                    do! transaction.RollbackAsync(CancellationToken.None)
                with _ ->
                    ()

                return raise error
        }

    /// Header-only recovery read for authority, admission, and capacity paths. Attempt evidence is
    /// intentionally loaded only by explicit detail inspection.
    let readHeader
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction option)
        (operationId: Guid)
        : Task<RetainedPreparation option> =
        task {
            use command =
                new NpgsqlCommand(select + " WHERE p.operation_id = @operation", connection)

            transaction |> Option.iter (fun value -> command.Transaction <- value)
            Sql.uuid command "operation" operationId
            let! result = command.ExecuteReaderAsync()
            use reader = result
            let! exists = reader.ReadAsync()
            return if exists then Some(read reader) else None
        }

    let sameImmutable (draft: RecoveryPreparationDraft) (existing: RetainedPreparation) =
        draft.CanonicalRequestFormat = existing.CanonicalRequestFormat
        && draft.RequestSha256 = existing.RequestSha256
        && draft.CanonicalRequest = existing.CanonicalRequest

    let readPendingCapacity
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction option)
        : Task<int64 * int64> =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT count(*), COALESCE(sum(octet_length(p.canonical_request)), 0)::bigint "
                    + "FROM claimcore.request_preparations p "
                    + "WHERE NOT EXISTS (SELECT 1 FROM claimcore.case_changes accepted "
                    + "                  WHERE accepted.operation_id = p.operation_id) "
                    + "AND NOT EXISTS (SELECT 1 FROM claimcore.operation_revocations revoked "
                    + "                WHERE revoked.operation_id = p.operation_id)",
                    connection
                )

            transaction |> Option.iter (fun value -> command.Transaction <- value)

            let! result = command.ExecuteReaderAsync()
            use reader = result
            let! hasRow = reader.ReadAsync()

            if not hasRow then
                raise (InvalidDataException("Preparation capacity could not be read."))

            return reader.GetInt64(0), reader.GetInt64(1)
        }

    let readCapacity
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        : Task<int64 * int64> =
        readPendingCapacity connection (Some transaction)

    let private bindPreparationInsert (command: NpgsqlCommand) (draft: RecoveryPreparationDraft) =
        Sql.uuid command "operation" draft.OperationId
        let format = command.Parameters.Add("format", NpgsqlDbType.Smallint)
        format.Value <- int16 draft.CanonicalRequestFormat
        Sql.text command "digest" draft.RequestSha256
        Sql.add command "request" NpgsqlDbType.Bytea (box draft.CanonicalRequest)
        Sql.text command "application" draft.PreparingApplicationVersion
        Sql.text command "fingerprint" draft.PreparingContractFingerprint

        Sql.text command "kind" (PreparingContractKindEncoding.token draft.PreparingContractKind)

    let insertPreparation
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (draft: RecoveryPreparationDraft)
        : Task<RetainedPreparation> =
        task {
            use command =
                new NpgsqlCommand(
                    """
                    INSERT INTO claimcore.request_preparations (
                        operation_id, canonical_request_format, request_sha256, canonical_request,
                        prepared_application_version, preparing_contract_fingerprint,
                        preparing_contract_kind
                    ) VALUES (
                        @operation, @format, @digest, @request, @application, @fingerprint, @kind
                    ) RETURNING prepared_at
                    """,
                    connection,
                    transaction
                )

            bindPreparationInsert command draft

            let! result = command.ExecuteReaderAsync()
            use reader = result
            let! inserted = reader.ReadAsync()

            if not inserted then
                raise (InvalidDataException("Preparation insert did not return its timestamp."))

            return
                ({
                    OperationId = draft.OperationId
                    CanonicalRequestFormat = draft.CanonicalRequestFormat
                    RequestSha256 = draft.RequestSha256
                    CanonicalRequest = Array.copy draft.CanonicalRequest
                    PreparedAt = reader.GetFieldValue<DateTimeOffset>(0)
                    PreparingApplicationVersion = draft.PreparingApplicationVersion
                    PreparingContractFingerprint = draft.PreparingContractFingerprint
                    PreparingContractKind = draft.PreparingContractKind
                    Lifecycle = PreparationLifecycle.Unsubmitted
                }
                : RetainedPreparation)
        }

    let insertMarker
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (operationId: Guid)
        (state: string)
        : Task<DateTimeOffset option> =
        task {
            use command =
                new NpgsqlCommand(
                    """
                    INSERT INTO claimcore.request_preparation_lifecycle (operation_id, state)
                    VALUES (@operation, @state)
                    ON CONFLICT (operation_id) DO NOTHING
                    RETURNING recorded_at
                    """,
                    connection,
                    transaction
                )

            Sql.uuid command "operation" operationId
            Sql.text command "state" state
            let! result = command.ExecuteReaderAsync()
            use reader = result
            let! inserted = reader.ReadAsync()

            return
                if inserted then
                    Some(reader.GetFieldValue<DateTimeOffset>(0))
                else
                    None
        }

    let asDismissal (preparation: RetainedPreparation) =
        match preparation.Lifecycle with
        | PreparationLifecycle.Unsubmitted ->
            invalidOp "A dismissal outcome requires a lifecycle marker."
        | PreparationLifecycle.SubmissionStarted _ ->
            RecoveryDismissal.SubmissionAlreadyStarted preparation
        | PreparationLifecycle.Dismissed _ -> RecoveryDismissal.AlreadyDismissed preparation
