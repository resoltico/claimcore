namespace ClaimCore.Postgres

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open OperationAuthorityStore
open PreparationData
open PreparationLifecycleStore
open SubmissionAttemptStore
open WitnessProtocolReconciliation

/// Witnessed admission of technical submission attempts and their settlements.
module internal RecoveryStartStore =
    let private requireActorAndWitness
        (actorContext: ActorCallContext option)
        (witness: WitnessProtocol option)
        ct
        =
        task {
            let actor =
                actorContext
                |> Option.defaultWith (fun () ->
                    invalidOp "Actor is required for attempt admission.")

            let active =
                witness
                |> Option.defaultWith (fun () ->
                    invalidOp "Witness is required for attempt admission.")

            do! active.Admit(ct)
            return actor, active
        }

    let private startWithoutAccepted
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        maximumAttempts
        (operationId: Guid)
        (header: RetainedPreparation)
        (actorContext: ActorCallContext)
        (witness: WitnessProtocol)
        (pending: (Guid * WitnessIntent) option ref)
        ct
        =
        task {
            let! revoked = find connection transaction operationId

            match revoked with
            | Some value when matches header.RequestSha256 value ->
                return
                    Ok(
                        RecoveryStart.Dismissed
                            { header with
                                Lifecycle = PreparationLifecycle.Dismissed value.RevokedAt
                            }
                    )
            | Some _ -> return Error RecoveryStoreFailure.IdempotencyConflict
            | None ->
                return!
                    SubmissionAttemptStore.start
                        maximumAttempts
                        connection
                        transaction
                        operationId
                        header
                        actorContext
                        witness
                        pending
                        ct
        }

    let private startResource operationId (actorContext: ActorCallContext) =
        match actorContext.Action, actorContext.CaseId with
        | EndpointAction.ExecuteNewCase, _ -> Some ResourceScope.Installation
        | EndpointAction.ExecuteCommand, Some caseId -> Some(ResourceScope.Case caseId)
        | EndpointAction.RecoveryResolve, Some caseId ->
            Some(ResourceScope.Operation(operationId, caseId))
        | _ -> None

    let private startAuthorized
        connection
        transaction
        maximumAttempts
        operationId
        (actorContext: ActorCallContext)
        (witness: WitnessProtocol)
        (pending: (Guid * WitnessIntent) option ref)
        ct
        =
        task {
            let! preparation = readHeader connection (Some transaction) operationId

            match preparation with
            | None -> return Error RecoveryStoreFailure.NotFound
            | Some header when
                actorContext.Action <> EndpointAction.ExecuteNewCase
                && actorContext.CaseId <> Some header.CaseId
                ->
                return Error RecoveryStoreFailure.ResourceUnavailable
            | Some header ->
                do! WitnessTechnical.reconcilePrepare witness connection transaction header ct

                let! accepted =
                    RecoveryAcceptedObservation.read
                        connection
                        transaction
                        witness
                        operationId
                        header.RequestSha256
                        ct

                match accepted with
                | Ok(Some receipt) -> return Ok(RecoveryStart.ObservedAccepted receipt)
                | Error failure -> return Error failure
                | Ok None ->
                    return!
                        startWithoutAccepted
                            connection
                            transaction
                            maximumAttempts
                            operationId
                            header
                            actorContext
                            witness
                            pending
                            ct
        }

    let private availableCase connection transaction (actorContext: ActorCallContext) ct =
        task {
            let! available =
                match actorContext.CaseId with
                | Some id -> ActorGrantGateQueries.availableCase connection transaction id ct
                | None -> task { return false }

            return available
        }

    let private startInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        maximumAttempts
        (operationId: Guid)
        (actorContext: ActorCallContext)
        (witness: WitnessProtocol)
        (pending: (Guid * WitnessIntent) option ref)
        ct
        =
        task {
            let! revision = ActorGrantRead.lockRevision connection transaction true ct

            do!
                Sql.lockKeyAsync
                    connection
                    transaction
                    ("operation:" + operationId.ToString("D"))
                    ct

            match startResource operationId actorContext with
            | None -> return Error RecoveryStoreFailure.ResourceUnavailable
            | Some resource ->
                let! available = availableCase connection transaction actorContext ct

                let! allowed =
                    ActorMutationGuard.authorizeScope
                        connection
                        transaction
                        actorContext
                        resource
                        revision

                if not available || not allowed then
                    return Error RecoveryStoreFailure.ResourceUnavailable
                else
                    return!
                        startAuthorized
                            connection
                            transaction
                            maximumAttempts
                            operationId
                            actorContext
                            witness
                            pending
                            ct
        }

    let start
        (dataSource: NpgsqlDataSource)
        (limits: PreparationLimits)
        (operationId: Guid)
        (cancellationToken: CancellationToken)
        (actorContext: ActorCallContext option)
        (witness: WitnessProtocol option)
        : Task<Result<RecoveryStart, RecoveryStoreFailure>> =
        task {
            if operationId = Guid.Empty then
                return Error(RecoveryStoreFailure.InvalidInput "operationId")
            else
                let commitStarted = ref false
                let pending: (Guid * WitnessIntent) option ref = ref None

                try
                    let! actor, active =
                        requireActorAndWitness actorContext witness cancellationToken

                    use! connection =
                        RuntimeDatabase.openConnectionAsyncWithCancellation
                            dataSource
                            cancellationToken

                    use! _authorityLease =
                        AuthorityOperationFence.acquireShared
                            (Some dataSource)
                            connection
                            cancellationToken

                    let! result =
                        withTransaction
                            connection
                            cancellationToken
                            commitStarted
                            (fun transaction ->
                                startInTransaction
                                    connection
                                    transaction
                                    limits.MaximumAttemptsPerOperation
                                    operationId
                                    actor
                                    active
                                    pending
                                    cancellationToken)

                    match pending.Value with
                    | Some(eventId, intent) ->
                        let! _ = active.SettleAuthority(eventId, intent) in ()
                    | None -> ()

                    return result
                with error ->
                    return
                        Error(mutationFailure (commitStarted.Value || pending.Value.IsSome) error)
        }


    let settle
        (dataSource: NpgsqlDataSource)
        (attemptId: Guid)
        outcome
        (cancellationToken: CancellationToken)
        : Task<Result<unit, RecoveryStoreFailure>> =
        task {
            if attemptId = Guid.Empty then
                return Error(RecoveryStoreFailure.InvalidInput "attemptId")
            else
                let commitStarted = ref false

                try
                    use! connection =
                        RuntimeDatabase.openConnectionAsyncWithCancellation
                            dataSource
                            cancellationToken

                    use! _authorityLease =
                        AuthorityOperationFence.acquireShared
                            (Some dataSource)
                            connection
                            cancellationToken

                    return!
                        withTransaction
                            connection
                            cancellationToken
                            commitStarted
                            (fun transaction ->
                                SubmissionAttemptStore.settle
                                    connection
                                    transaction
                                    attemptId
                                    outcome)
                with error ->
                    return Error(mutationFailure commitStarted.Value error)
        }
