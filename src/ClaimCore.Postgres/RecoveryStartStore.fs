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

/// Witnessed admission of technical submission attempts and their settlements.
module internal RecoveryStartStore =
    let private requireActorAndWitness
        (actorContext: ActorCallContext option)
        (witness: WitnessProtocol option)
        =
        let actor =
            actorContext
            |> Option.defaultWith (fun () -> invalidOp "Actor is required for attempt admission.")

        let active =
            witness
            |> Option.defaultWith (fun () -> invalidOp "Witness is required for attempt admission.")

        active.Admit()
        actor, active

    let private startWithoutAccepted
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        maximumAttempts
        (operationId: Guid)
        (header: RetainedPreparation)
        (actorContext: ActorCallContext)
        (witness: WitnessProtocol)
        (pending: (Guid * WitnessIntent) option ref)
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
                WitnessTechnical.reconcilePrepare witness connection transaction header
                let! accepted = StoreData.readOperation connection (Some transaction) operationId

                match accepted with
                | Some(receipt, fingerprint) when fingerprint = header.RequestSha256 ->
                    return Ok(RecoveryStart.ObservedAccepted receipt)
                | Some _ -> return Error RecoveryStoreFailure.IdempotencyConflict
                | None ->
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
        }

    let private startInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        maximumAttempts
        (operationId: Guid)
        (actorContext: ActorCallContext)
        (witness: WitnessProtocol)
        (pending: (Guid * WitnessIntent) option ref)
        =
        task {
            let! revision =
                ActorGrantRead.lockRevision connection transaction true CancellationToken.None

            do! Sql.lockKeyAsync connection transaction ("operation:" + operationId.ToString("D"))

            match startResource operationId actorContext with
            | None -> return Error RecoveryStoreFailure.ResourceUnavailable
            | Some resource ->
                let! available =
                    match actorContext.CaseId with
                    | Some id ->
                        ActorGrantGateQueries.availableCase
                            connection
                            transaction
                            id
                            CancellationToken.None
                    | None -> task { return false }

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
                    let actor, active = requireActorAndWitness actorContext witness

                    use! connection = RuntimeDatabase.openConnectionAsync dataSource

                    use! _authorityLease =
                        AuthorityOperationFence.acquireShared
                            (Some dataSource)
                            connection
                            System.Threading.CancellationToken.None

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
                                    pending)

                    match pending.Value with
                    | Some(eventId, intent) -> active.SettleAuthority(eventId, intent) |> ignore
                    | None -> ()

                    return result
                with error ->
                    return
                        match pending.Value, error with
                        | Some _, _
                        | _, :? WitnessPending ->
                            Error RecoveryStoreFailure.TechnicalMutationUnknown
                        | _ -> Error(mutationFailure commitStarted.Value error)
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
                    use! connection = RuntimeDatabase.openConnectionAsync dataSource

                    use! _authorityLease =
                        AuthorityOperationFence.acquireShared
                            (Some dataSource)
                            connection
                            System.Threading.CancellationToken.None

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
