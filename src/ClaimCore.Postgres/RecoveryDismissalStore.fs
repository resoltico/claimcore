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
                    Ok(
                        RecoveryDismissal.AlreadyDismissed(
                            { header with
                                Lifecycle = PreparationLifecycle.Dismissed value.RevokedAt
                            }
                        ),
                        None
                    )
                | None -> Ok(RecoveryDismissal.RevokedTombstone(project value), None)
        }

    let private createRevocation
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (operationId: Guid)
        requestSha256
        (actorEvidence: RevocationActorEvidence)
        (witness: WitnessProtocol)
        ct
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
                let! intent = witness.BeginRevocation(operationId, requestSha256, actorEvidence, ct)

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
                        Ok(
                            RecoveryDismissal.Dismissed(
                                { header with
                                    Lifecycle = PreparationLifecycle.Dismissed value.RevokedAt
                                }
                            ),
                            Some intent
                        )
                    | None -> Error RecoveryStoreFailure.TechnicalMutationUnknown
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
        ct
        =
        task {
            let! accepted =
                RecoveryAcceptedObservation.read
                    connection
                    transaction
                    witness
                    operationId
                    requestSha256
                    ct

            match accepted with
            | Error failure -> return Error failure
            | Ok(Some receipt) -> return Ok(RecoveryDismissal.ObservedAccepted receipt, None)
            | Ok None ->
                let! revoked = find connection transaction operationId

                match revoked with
                | Some value when not (matches requestSha256 value) ->
                    return Error RecoveryStoreFailure.IdempotencyConflict
                | Some value ->
                    do! witness.ReconcileRevoked(connection, transaction, operationId, ct)
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
                            ct
        }

    let private dismissInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (operationId: Guid)
        requestSha256
        (actorContext: ActorCallContext)
        revision
        (witness: WitnessProtocol)
        ct
        =
        task {
            do!
                Sql.lockKeyAsync
                    connection
                    transaction
                    ("operation:" + operationId.ToString("D"))
                    ct

            let! allowed =
                ActorMutationGuard.authorizeDismissal
                    connection
                    transaction
                    operationId
                    revision
                    actorContext

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
                        ct
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
            do! active.Admit(cancellationToken)

            use! connection =
                RuntimeDatabase.openConnectionAsyncWithCancellation dataSource cancellationToken

            use! _authorityLease =
                AuthorityOperationFence.acquireShared (Some dataSource) connection cancellationToken

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
                    cancellationToken

            match result with
            | Error failure -> return Error failure
            | Ok(outcome, intent) ->
                do!
                    commitBoundary cancellationToken commitStarted (fun () ->
                        transaction.CommitAsync(CancellationToken.None))

                match intent with
                | Some value -> let! _ = active.SettleRevoked(value.Ticket.OperationId, value) in ()
                | None -> ()

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

                    match actorContext with
                    | None -> return Error RecoveryStoreFailure.ResourceUnavailable
                    | Some actor ->
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
                    return Error(mutationFailure commitStarted.Value error)
        }
