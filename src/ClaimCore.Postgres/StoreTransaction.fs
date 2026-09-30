namespace ClaimCore.Postgres

open System
open System.Data
open System.IO
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat
open WitnessProtocolReconciliation
open StoreTransactionPersistence

/// Owns the PostgreSQL transaction protocol around the application's domain decision.
module internal StoreTransaction =
    let private attribution (actorContext: ActorCallContext) caseId =
        {
            Command =
                {
                    Actor = actorContext.Binding
                    CaseId = caseId
                }
            PreparerActorId = actorContext.Binding.ActorId
            ImporterActorId = None
            Phase = AttemptActorPhase.NormalSubmit
        }

    let private decideAndPersist
        connection
        transaction
        operation
        decide
        current
        caseId
        actorContext
        witness
        commitStarted
        =
        task {
            match decide current with
            | Error error -> return Error(CoreFailure.Domain error)
            | Ok(claim, context) ->
                return!
                    persistDecision
                        connection
                        transaction
                        operation
                        context
                        current
                        caseId
                        (attribution actorContext caseId)
                        claim
                        witness
                        commitStarted
        }

    let private applyDecision
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        operation
        decide
        (actorContext: ActorCallContext)
        revision
        (witness: WitnessProtocol)
        commitStarted
        =
        task {
            let request = Operation.request operation
            do! Sql.lockKeyAsync connection transaction ("case:" + request.CaseReference)
            let! current = StoreData.readCase connection transaction request.CaseReference

            let! caseId =
                StoreData.caseIdForDecision
                    connection
                    transaction
                    request.CaseReference
                    current
                    actorContext.CaseId
                    (match request.Command with
                     | Command.Open _ -> true
                     | _ -> false)

            let! allowed =
                ActorMutationGuard.authorize
                    connection
                    transaction
                    actorContext
                    request
                    caseId
                    revision

            if not allowed then
                return Error CoreFailure.ResourceUnavailable
            else
                return!
                    decideAndPersist
                        connection
                        transaction
                        operation
                        decide
                        current
                        caseId
                        actorContext
                        witness
                        commitStarted
        }

    let private observeOrApply
        connection
        transaction
        operation
        decide
        actorContext
        revision
        (witness: WitnessProtocol)
        commitStarted
        =
        task {
            let request = Operation.request operation
            let fingerprint = Operation.fingerprint operation

            let! observed =
                StoreData.readOperation connection (Some transaction) request.OperationId

            match observed with
            | Some(receipt, original) when original = fingerprint ->
                witness.ReconcileAccepted(connection, transaction, request.OperationId)
                return Ok receipt
            | Some _ -> return Error CoreFailure.IdempotencyConflict
            | None ->
                return!
                    applyDecision
                        connection
                        transaction
                        operation
                        decide
                        actorContext
                        revision
                        witness
                        commitStarted
        }

    let private execute
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        operation
        decide
        (actorContext: ActorCallContext)
        (witness: WitnessProtocol)
        commitStarted
        =
        task {
            let request = Operation.request operation

            let! revision =
                ActorGrantRead.lockRevision
                    connection
                    transaction
                    true
                    Threading.CancellationToken.None

            do!
                Sql.lockKeyAsync
                    connection
                    transaction
                    ("operation:" + request.OperationId.ToString("D"))

            match actorContext.CaseId with
            | None -> return Error CoreFailure.ResourceUnavailable
            | Some id ->
                let! allowed =
                    ActorMutationGuard.authorize
                        connection
                        transaction
                        actorContext
                        request
                        id
                        revision

                if not allowed then
                    return Error CoreFailure.ResourceUnavailable
                else
                    return!
                        observeOrApply
                            connection
                            transaction
                            operation
                            decide
                            actorContext
                            revision
                            witness
                            commitStarted
        }

    let transact
        (dataSource: NpgsqlDataSource)
        (witness: WitnessProtocol option)
        (actorContext: ActorCallContext option)
        operation
        decide
        =
        task {
            let request = Operation.request operation
            let commitStarted = ref false

            try
                match witness, actorContext with
                | None, _ -> return Error CoreFailure.StoreUnavailable
                | _, None -> return Error CoreFailure.ResourceUnavailable
                | Some active, Some actor ->
                    active.Admit()
                    use! connection = RuntimeDatabase.openConnectionAsync dataSource

                    use! _authorityLease =
                        AuthorityOperationFence.acquireShared
                            (Some dataSource)
                            connection
                            System.Threading.CancellationToken.None

                    let! transaction =
                        connection.BeginTransactionAsync(IsolationLevel.ReadCommitted)

                    use _ = transaction

                    return!
                        execute connection transaction operation decide actor active commitStarted
            with
            | UnsupportedPostgresVersion
            | RuntimeDatabaseMismatch -> return Error CoreFailure.SchemaMismatch
            | :? NpgsqlException as error ->
                return Error(StoreData.failure commitStarted.Value request.OperationId error)
            | :? TimeoutException as error ->
                return Error(StoreData.failure commitStarted.Value request.OperationId error)
            | :? InvalidDataException as error ->
                return Error(StoreData.failure commitStarted.Value request.OperationId error)
            | :? InvalidCastException as error ->
                return Error(StoreData.failure commitStarted.Value request.OperationId error)
            | WitnessPending -> return Error(CoreFailure.CommitOutcomeUnknown request.OperationId)
            | :? InvalidOperationException as error ->
                return Error(StoreData.failure commitStarted.Value request.OperationId error)
        }
