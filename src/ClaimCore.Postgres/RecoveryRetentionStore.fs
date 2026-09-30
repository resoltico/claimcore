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

/// Retention and attempt admission use the operation lock, with capacity acquired before it.
module internal RecoveryRetentionStore =
    let private matchesRetainActor
        (draft: RecoveryPreparationDraft)
        (actorContext: ActorCallContext)
        =
        draft.CaseId = (actorContext.CaseId |> Option.defaultValue Guid.Empty)
        && draft.PreparerGrantRevision > 0L
        && (if actorContext.Action = EndpointAction.RecoveryImportRetain then
                draft.ImporterActorId = Some actorContext.Binding.ActorId
            else
                draft.ImporterActorId.IsNone
                && draft.PreparerActorId = actorContext.Binding.ActorId
                && draft.PreparerGrantRevision = actorContext.Binding.GrantRevision)

    let private requireActorAndWitness
        (actorContext: ActorCallContext option)
        (witness: WitnessProtocol option)
        =
        let actor =
            actorContext
            |> Option.defaultWith (fun () -> invalidOp "Actor is required for preparation.")

        let active =
            witness
            |> Option.defaultWith (fun () -> invalidOp "Witness is required for preparation.")

        active.Admit()
        actor, active

    let private retainAbsent
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (limits: PreparationLimits)
        (draft: RecoveryPreparationDraft)
        (witness: WitnessProtocol)
        (pending: (Guid * WitnessIntent) option ref)
        =
        task {
            match PreparationIntegrity.validateDraft draft with
            | Error failure -> return Error failure
            | Ok() ->
                let! count, bytes = readCapacity connection transaction

                if
                    count >= int64 limits.MaximumPreparations
                    || bytes + int64 draft.CanonicalRequest.Length >
                        limits.MaximumCanonicalRequestBytes
                then
                    return Error RecoveryStoreFailure.CapacityExceeded
                else
                    let eventId, intent = WitnessTechnical.beginPrepare witness draft
                    pending.Value <- Some(eventId, intent)
                    let! inserted = insertPreparation connection transaction draft eventId intent
                    return Ok(RecoveryRetain.Created inserted)
        }

    let private retainWithoutAccepted
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (limits: PreparationLimits)
        (draft: RecoveryPreparationDraft)
        (request: CommandRequest)
        (witness: WitnessProtocol)
        (pending: (Guid * WitnessIntent) option ref)
        =
        task {
            let! revoked = find connection transaction draft.OperationId

            match revoked with
            | Some value when matches draft.RequestSha256 value ->
                return Ok(RecoveryRetain.Revoked(project value))
            | Some _ -> return Error RecoveryStoreFailure.IdempotencyConflict
            | None ->
                let! existing = readHeader connection (Some transaction) draft.OperationId

                match existing with
                | Some value when value.PreparerActorId <> draft.PreparerActorId ->
                    return Error RecoveryStoreFailure.ResourceUnavailable
                | Some value when
                    (match request.Command with
                     | Command.Open _ -> false
                     | _ -> value.CaseId <> draft.CaseId)
                    ->
                    return Error RecoveryStoreFailure.ResourceUnavailable
                | Some value when sameImmutable draft value ->
                    WitnessTechnical.reconcilePrepare witness connection transaction value
                    return Ok(RecoveryRetain.Existing value)
                | Some _ -> return Error RecoveryStoreFailure.IdempotencyConflict
                | None -> return! retainAbsent connection transaction limits draft witness pending
        }

    let private retainInTransaction
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (limits: PreparationLimits)
        (draft: RecoveryPreparationDraft)
        (request: CommandRequest)
        (actorContext: ActorCallContext)
        (witness: WitnessProtocol)
        (pending: (Guid * WitnessIntent) option ref)
        =
        task {
            let! revision =
                ActorGrantRead.lockRevision connection transaction true CancellationToken.None

            do! Sql.lockKeyAsync connection transaction "claimcore:request-preparation-capacity"

            do!
                Sql.lockKeyAsync
                    connection
                    transaction
                    ("operation:" + draft.OperationId.ToString("D"))

            do! Sql.lockKeyAsync connection transaction ("case:" + request.CaseReference)

            let! authorized =
                ActorMutationGuard.authorize
                    connection
                    transaction
                    actorContext
                    request
                    draft.CaseId
                    revision

            if not (matchesRetainActor draft actorContext) || not authorized then
                return Error RecoveryStoreFailure.ResourceUnavailable
            else
                let! accepted =
                    StoreData.readOperation connection (Some transaction) draft.OperationId

                match accepted with
                | Some(receipt, original) when original = draft.RequestSha256 ->
                    return Ok(RecoveryRetain.ObservedAccepted receipt)
                | Some _ -> return Error RecoveryStoreFailure.IdempotencyConflict
                | None ->
                    return!
                        retainWithoutAccepted
                            connection
                            transaction
                            limits
                            draft
                            request
                            witness
                            pending
        }

    let retain
        (dataSource: NpgsqlDataSource)
        (limits: PreparationLimits)
        (draft: RecoveryPreparationDraft)
        (cancellationToken: CancellationToken)
        (actorContext: ActorCallContext option)
        (witness: WitnessProtocol option)
        : Task<Result<RecoveryRetain, RecoveryStoreFailure>> =
        task {
            match PreparationIntegrity.validateIdentity draft with
            | Error failure -> return Error failure
            | Ok request ->
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
                                retainInTransaction
                                    connection
                                    transaction
                                    limits
                                    draft
                                    request
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
