namespace ClaimCore.Postgres

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open OperationAuthorityStore
open PreparationData
open WitnessProtocolReconciliation

/// Durable dismissal closes future execution authority but keeps prior attempt knowledge intact.
module internal RecoveryDismissalStore =
    exception private ActorUnavailable

    let private dismissed header value =
        { header with
            Lifecycle = PreparationLifecycle.Dismissed value.RevokedAt
        }

    let private existingRevocation
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (operationId: Guid)
        value
        =
        task {
            let! preparation = readHeader connection (Some transaction) operationId

            return
                match preparation with
                | Some header ->
                    Ok(RecoveryDismissal.AlreadyDismissed(dismissed header value), None)
                | None -> Ok(RecoveryDismissal.RevokedTombstone(project value), None)
        }

    let private createRevocation
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (operationId: Guid)
        requestSha256
        (actorEvidence: RevocationActorEvidence)
        (witness: WitnessProtocol)
        =
        task {
            let! preparation = readHeader connection (Some transaction) operationId

            match preparation with
            | None -> return Error RecoveryStoreFailure.NotFound
            | Some header when header.RequestSha256 <> requestSha256 ->
                return Error RecoveryStoreFailure.IdempotencyConflict
            | Some header when header.CaseId <> actorEvidence.CaseId ->
                return Error RecoveryStoreFailure.ResourceUnavailable
            | Some header ->
                let intent = witness.BeginRevocation(operationId, requestSha256, actorEvidence)

                let! inserted =
                    insert
                        connection
                        transaction
                        operationId
                        requestSha256
                        actorEvidence
                        RevocationReason.OperatorDismissal
                        intent.Ticket

                return
                    match inserted with
                    | Some value ->
                        Ok(RecoveryDismissal.Dismissed(dismissed header value), Some intent)
                    | None -> Error RecoveryStoreFailure.TechnicalMutationUnknown
        }

    let private authorized
        connection
        transaction
        operationId
        revision
        (actorContext: ActorCallContext)
        =
        task {
            match actorContext.CaseId with
            | None -> return false
            | Some caseId ->
                let! available =
                    ActorGrantGateQueries.availableCase
                        connection
                        transaction
                        caseId
                        CancellationToken.None

                let resource = ResourceScope.Operation(operationId, caseId)

                let! current =
                    ActorGrantRead.loadUnderLock
                        connection
                        transaction
                        actorContext.Binding.Principal
                        resource
                        revision
                        CancellationToken.None

                return
                    available
                    && current
                       |> Option.exists (fun value ->
                           match
                               ActorAuthorization.authorizeAtRevision
                                   actorContext.Binding.Principal
                                   value
                                   actorContext.Binding.GrantRevision
                                   EndpointAction.RecoveryDismiss
                                   resource
                           with
                           | AuthorizationDecision.Available(actorId, _) ->
                               actorId = actorContext.Binding.ActorId
                           | AuthorizationDecision.Unavailable -> false)
        }

    let private actorEvidence (actorContext: ActorCallContext) =
        {
            CaseId =
                actorContext.CaseId
                |> Option.defaultWith (fun () ->
                    raise (InvalidDataException("Revocation case identity is absent.")))
            RevokingActorId = actorContext.Binding.ActorId
            GrantRevision = actorContext.Binding.GrantRevision
        }

    let private dismissAuthorized
        connection
        transaction
        operationId
        requestSha256
        (actorContext: ActorCallContext)
        (witness: WitnessProtocol)
        =
        task {
            let! accepted = StoreData.readOperation connection (Some transaction) operationId

            match accepted with
            | Some(_, fingerprint) when fingerprint <> requestSha256 ->
                return Error RecoveryStoreFailure.IdempotencyConflict
            | Some(receipt, _) -> return Ok(RecoveryDismissal.ObservedAccepted receipt, None)
            | None ->
                let! revoked = find connection transaction operationId

                match revoked with
                | Some value when not (matches requestSha256 value) ->
                    return Error RecoveryStoreFailure.IdempotencyConflict
                | Some value ->
                    witness.ReconcileRevoked(connection, transaction, operationId)
                    return! existingRevocation connection transaction operationId value
                | None ->
                    return!
                        createRevocation
                            connection
                            transaction
                            operationId
                            requestSha256
                            (actorEvidence actorContext)
                            witness
        }

    let private dismissInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (operationId: Guid)
        requestSha256
        (actorContext: ActorCallContext)
        revision
        (witness: WitnessProtocol)
        =
        task {
            do! Sql.lockKeyAsync connection transaction ("operation:" + operationId.ToString("D"))

            let! allowed = authorized connection transaction operationId revision actorContext

            if not allowed then
                return Error RecoveryStoreFailure.ResourceUnavailable
            else
                return!
                    dismissAuthorized
                        connection
                        transaction
                        operationId
                        requestSha256
                        actorContext
                        witness
        }

    let private dismissWithContext
        dataSource
        operationId
        requestSha256
        cancellationToken
        (active: WitnessProtocol)
        actor
        (commitStarted: bool ref)
        =
        task {
            active.Admit()
            use! connection = RuntimeDatabase.openConnectionAsync dataSource

            use! _authorityLease =
                AuthorityOperationFence.acquireShared
                    (Some dataSource)
                    connection
                    System.Threading.CancellationToken.None

            let! transaction =
                connection.BeginTransactionAsync(
                    System.Data.IsolationLevel.ReadCommitted,
                    cancellationToken
                )

            use _ = transaction

            let! revision =
                ActorGrantRead.lockRevision connection transaction true cancellationToken

            let! result =
                dismissInTransaction
                    connection
                    transaction
                    operationId
                    requestSha256
                    actor
                    revision
                    active

            match result with
            | Error failure -> return Error failure
            | Ok(outcome, intent) ->
                do!
                    commitBoundary cancellationToken commitStarted (fun () ->
                        transaction.CommitAsync(CancellationToken.None))

                intent
                |> Option.iter (fun value -> active.SettleRevoked(operationId, value) |> ignore)

                return Ok outcome
        }

    let dismiss
        (dataSource: NpgsqlDataSource)
        (operationId: Guid)
        requestSha256
        (cancellationToken: CancellationToken)
        (witness: WitnessProtocol option)
        (actorContext: ActorCallContext option)
        : Task<Result<RecoveryDismissal, RecoveryStoreFailure>> =
        task {
            if operationId = Guid.Empty || String.IsNullOrWhiteSpace(requestSha256) then
                return Error(RecoveryStoreFailure.InvalidInput "operationId")
            else
                let commitStarted = ref false

                try
                    let active =
                        witness
                        |> Option.defaultWith (fun () ->
                            invalidOp "Witness is required for revocation.")

                    let actor =
                        actorContext |> Option.defaultWith (fun () -> raise ActorUnavailable)

                    return!
                        dismissWithContext
                            dataSource
                            operationId
                            requestSha256
                            cancellationToken
                            active
                            actor
                            commitStarted
                with error ->
                    return
                        match error with
                        | ActorUnavailable -> Error RecoveryStoreFailure.ResourceUnavailable
                        | :? WitnessPending -> Error RecoveryStoreFailure.TechnicalMutationUnknown
                        | _ -> Error(mutationFailure commitStarted.Value error)
        }
