namespace ClaimCore.Postgres

open System
open System.Data
open System.IO
open System.Threading
open Npgsql
open ClaimCore.Application

module internal ActorGrantGateQueries =
    let private caseId connection transaction sql name value cancellationToken =
        task {
            use command = new NpgsqlCommand(sql, connection, transaction)
            Sql.uuid command name value
            let! found = command.ExecuteScalarAsync(cancellationToken)

            return
                match found with
                | :? Guid as id when id <> Guid.Empty -> Some id
                | _ -> None
        }

    let caseIdByReference connection transaction reference cancellationToken =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT case_id FROM claimcore.cases WHERE case_reference=@reference "
                    + "AND disposition='ACTIVE' AND privacy_phase='ACTIVE'",
                    connection,
                    transaction
                )

            Sql.text command "reference" reference
            let! found = command.ExecuteScalarAsync(cancellationToken)

            return
                match found with
                | :? Guid as id when id <> Guid.Empty -> Some id
                | _ -> None
        }

    let caseIdByReferenceAny connection transaction reference cancellationToken =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT case_id FROM claimcore.cases WHERE case_reference=@reference",
                    connection,
                    transaction
                )

            Sql.text command "reference" reference
            let! found = command.ExecuteScalarAsync(cancellationToken)

            return
                match found with
                | :? Guid as id when id <> Guid.Empty -> Some id
                | _ -> None
        }

    let blockedReference
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        reference
        (cancellationToken: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM claimcore.cases WHERE case_reference=@reference "
                    + "AND (disposition<>'ACTIVE' OR privacy_phase<>'ACTIVE'))",
                    connection,
                    transaction
                )

            Sql.text command "reference" reference
            let! found = command.ExecuteScalarAsync(cancellationToken)
            return found :?> bool
        }

    let availableCase
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (caseId: Guid)
        (cancellationToken: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT NOT EXISTS (SELECT 1 FROM claimcore.cases WHERE case_id=@case "
                    + "AND (disposition<>'ACTIVE' OR privacy_phase<>'ACTIVE'))",
                    connection,
                    transaction
                )

            Sql.uuid command "case" caseId
            let! found = command.ExecuteScalarAsync(cancellationToken)
            return found :?> bool
        }

    let acceptedCaseId connection transaction operationId cancellationToken =
        caseId
            connection
            transaction
            ("SELECT c.case_id FROM claimcore.case_changes c "
             + "JOIN claimcore.cases k ON k.case_id=c.case_id "
             + "WHERE c.operation_id=@operation AND k.disposition='ACTIVE' "
             + "AND k.privacy_phase='ACTIVE'")
            "operation"
            operationId
            cancellationToken

    let retainedCaseId connection transaction operationId cancellationToken =
        caseId
            connection
            transaction
            "SELECT case_id FROM claimcore.request_preparations WHERE operation_id=@operation"
            "operation"
            operationId
            cancellationToken

    let private operationIds
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (operationId: Guid)
        (cancellationToken: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT p.case_id,c.case_id,r.case_id "
                    + "FROM (SELECT @operation::uuid AS id) target "
                    + "LEFT JOIN claimcore.request_preparations p ON p.operation_id=target.id "
                    + "LEFT JOIN claimcore.case_changes c ON c.operation_id=target.id "
                    + "LEFT JOIN claimcore.operation_revocations r ON r.operation_id=target.id",
                    connection,
                    transaction
                )

            Sql.uuid command "operation" operationId
            let! result = command.ExecuteReaderAsync(cancellationToken)
            use reader = result
            let! found = reader.ReadAsync(cancellationToken)

            if not found then
                return []
            else
                return
                    [ 0..2 ]
                    |> List.choose (fun index ->
                        if reader.IsDBNull(index) then
                            None
                        else
                            Some(reader.GetGuid(index)))
                    |> List.distinct
        }

    let operationCaseId connection transaction operationId cancellationToken =
        task {
            // The first reader must close before the active-state query uses this connection.
            let! ids = operationIds connection transaction operationId cancellationToken

            if ids.Length > 1 then
                raise (InvalidDataException("Operation authority names inconsistent cases."))

            match ids |> List.tryHead with
            | None -> return None
            | Some id ->
                let! available = availableCase connection transaction id cancellationToken
                return if available then Some id else None
        }

    let authorize connection transaction revision principal action resource cancellationToken =
        task {
            let target = resource |> Option.defaultValue (ResourceScope.Case Guid.Empty)

            let! authority =
                ActorGrantRead.loadUnderLock
                    connection
                    transaction
                    principal
                    target
                    revision
                    cancellationToken

            return
                match resource, authority with
                | Some actual, Some value ->
                    match ActorAuthorization.authorize principal value action actual with
                    | AuthorizationDecision.Available(actorId, grantRevision) ->
                        Some(
                            {
                                Principal = principal
                                ActorId = actorId
                                GrantRevision = grantRevision
                            },
                            ActorActionAdvice.mayEditCommands principal value actual,
                            ActorActionAdvice.allowedRecoveryActions principal value actual
                        )
                    | AuthorizationDecision.Unavailable -> None
                | _ -> None
        }

    let openSnapshot (dataSource: NpgsqlDataSource) cancellationToken =
        task {
            let! connection =
                RuntimeDatabase.openConnectionAsyncWithCancellation dataSource cancellationToken

            try
                let! transaction =
                    connection.BeginTransactionAsync(
                        IsolationLevel.ReadCommitted,
                        cancellationToken
                    )

                try
                    let! revision =
                        ActorGrantRead.lockRevision connection transaction false cancellationToken

                    return connection, transaction, revision
                with error ->
                    transaction.Dispose()
                    return raise error
            with error ->
                connection.Dispose()
                return raise error
        }
