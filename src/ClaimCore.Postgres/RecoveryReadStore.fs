namespace ClaimCore.Postgres

open System
open System.Data
open System.IO
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open OperationAuthorityStore
open PreparationData

/// Read-only recovery views combine the retained header with current accepted/revoked authority.
module internal RecoveryReadStore =
    exception private ActorContextMissing

    open RecoveryReadAuthority

    let private pendingStored (preparation: RetainedPreparation option) =
        match preparation with
        | Some header ->
            match header.Lifecycle with
            | PreparationLifecycle.Dismissed _ ->
                raise (InvalidDataException("Dismissed preparation lacks revocation authority."))
            | PreparationLifecycle.Unsubmitted
            | PreparationLifecycle.SubmissionStarted _ ->
                Some(RecoveryStoredOperation.Retained(header, RecoveryAuthority.PendingAuthority))
        | None -> None

    let private classifyStored (preparation: RetainedPreparation option) accepted revoked =
        match preparation, accepted, revoked with
        | _, Some _, Some _ ->
            raise (InvalidDataException("Accepted and revoked authority coexist."))
        | Some header, Some(_, fingerprint), None when header.RequestSha256 = fingerprint ->
            Some(RecoveryStoredOperation.Retained(header, RecoveryAuthority.AcceptedAuthority))
        | Some _, Some _, None ->
            raise (InvalidDataException("Accepted operation identity disagrees with recovery."))
        | None, Some _, None -> None
        | Some header, None, Some revocation when matches header.RequestSha256 revocation ->
            Some(RecoveryStoredOperation.Retained(header, RecoveryAuthority.RevokedAuthority))
        | Some _, None, Some _ ->
            raise (InvalidDataException("Revoked operation identity disagrees with recovery."))
        | None, None, Some revocation ->
            Some(RecoveryStoredOperation.RevokedTombstone(project revocation))
        | preparation, None, None -> pendingStored preparation

    let private stored
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction option)
        (operationId: Guid)
        : Task<RecoveryStoredOperation option> =
        task {
            let! preparation = readHeader connection transaction operationId
            let! accepted = StoreData.readOperation connection transaction operationId

            let! revoked =
                match transaction with
                | Some value -> find connection value operationId
                | None -> findRead connection operationId

            return classifyStored preparation accepted revoked
        }

    let get
        (dataSource: NpgsqlDataSource)
        (operationId: Guid)
        (cancellationToken: CancellationToken)
        (actorContext: ActorCallContext option)
        : Task<Result<RecoveryStoredOperation option, RecoveryStoreFailure>> =
        task {
            if operationId = Guid.Empty then
                return Error(RecoveryStoreFailure.InvalidInput "operationId")
            else
                try
                    let context =
                        actorContext |> Option.defaultWith (fun () -> raise ActorContextMissing)

                    cancellationToken.ThrowIfCancellationRequested()
                    use! connection = RuntimeDatabase.openConnectionAsync dataSource
                    use transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead)

                    let! revision =
                        ActorGrantRead.lockRevision connection transaction false cancellationToken

                    let! scope = storedScope connection transaction operationId

                    let! authorized =
                        mayRead connection transaction revision operationId context scope

                    if not authorized then
                        return Error RecoveryStoreFailure.ResourceUnavailable
                    else
                        let! result = stored connection (Some transaction) operationId
                        cancellationToken.ThrowIfCancellationRequested()
                        return Ok result
                with
                | :? OperationCanceledException -> return Error RecoveryStoreFailure.ReadCancelled
                | ActorContextMissing -> return Error RecoveryStoreFailure.ResourceUnavailable
                | error -> return Error(fail error)
        }

    let private inspectSnapshot
        (dataSource: NpgsqlDataSource)
        (operationId: Guid)
        (after: RecoveryAttemptCursor option)
        pageSize
        (cancellationToken: CancellationToken)
        (context: ActorCallContext)
        =
        task {
            cancellationToken.ThrowIfCancellationRequested()
            use! connection = RuntimeDatabase.openConnectionAsync dataSource
            use transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead)

            let! revision =
                ActorGrantRead.lockRevision connection transaction false cancellationToken

            let! scope = storedScope connection transaction operationId
            let! authorized = mayRead connection transaction revision operationId context scope

            if not authorized then
                return Error RecoveryStoreFailure.ResourceUnavailable
            else
                let! result = stored connection (Some transaction) operationId

                match result with
                | None -> return Ok None
                | Some(RecoveryStoredOperation.RevokedTombstone revocation) ->
                    return Ok(Some(RecoveryStoreInspection.RevokedTombstone revocation))
                | Some(RecoveryStoredOperation.Retained(header, authority)) ->
                    let! evidence =
                        PreparationEvidence.readPage
                            connection
                            (Some transaction)
                            operationId
                            after
                            pageSize

                    cancellationToken.ThrowIfCancellationRequested()
                    return Ok(Some(RecoveryStoreInspection.Retained(header, evidence, authority)))
        }

    let inspect
        (dataSource: NpgsqlDataSource)
        (limits: PreparationLimits)
        (operationId: Guid)
        (after: RecoveryAttemptCursor option)
        pageSize
        (cancellationToken: CancellationToken)
        (actorContext: ActorCallContext option)
        : Task<Result<RecoveryStoreInspection option, RecoveryStoreFailure>> =
        task {
            if
                operationId = Guid.Empty
                || pageSize < 1
                || pageSize > limits.MaximumPageSize
                || after |> Option.exists (fun cursor -> cursor.OperationId <> operationId)
            then
                return Error(RecoveryStoreFailure.InvalidInput "recovery inspection")
            else
                try
                    let context =
                        actorContext |> Option.defaultWith (fun () -> raise ActorContextMissing)

                    return!
                        inspectSnapshot
                            dataSource
                            operationId
                            after
                            pageSize
                            cancellationToken
                            context
                with
                | :? OperationCanceledException -> return Error RecoveryStoreFailure.ReadCancelled
                | ActorContextMissing -> return Error RecoveryStoreFailure.ResourceUnavailable
                | error -> return Error(fail error)
        }

    let private listSnapshot
        (dataSource: NpgsqlDataSource)
        (limits: PreparationLimits)
        view
        (after: RecoveryCursor option)
        pageSize
        (actor: ActorCallContext)
        (ct: CancellationToken)
        =
        task {
            ct.ThrowIfCancellationRequested()
            use! connection = RuntimeDatabase.openConnectionAsync dataSource
            use transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead)

            let! revision = ActorGrantRead.lockRevision connection transaction false ct

            let! authorized =
                ActorMutationGuard.authorizeScope
                    connection
                    transaction
                    actor
                    ResourceScope.Installation
                    revision

            let! now = Sql.databaseNow connection transaction

            let cursorBound =
                after
                |> Option.forall (fun cursor ->
                    cursor.ActorId = actor.Binding.ActorId
                    && cursor.GrantRevision = actor.Binding.GrantRevision
                    && cursor.ExpiresAt > now)

            if not authorized || not cursorBound then
                return Error RecoveryStoreFailure.ResourceUnavailable
            else
                let! page =
                    PreparationPageStore.list
                        connection
                        limits
                        view
                        actor.Binding
                        (now.AddMinutes(5.0))
                        after
                        pageSize

                ct.ThrowIfCancellationRequested()
                do! transaction.CommitAsync(ct)
                return Ok page
        }

    let list
        (dataSource: NpgsqlDataSource)
        (limits: PreparationLimits)
        view
        (after: RecoveryCursor option)
        pageSize
        (cancellationToken: CancellationToken)
        (actorContext: ActorCallContext option)
        : Task<Result<RecoveryStorePage, RecoveryStoreFailure>> =
        task {
            if
                pageSize < 1
                || pageSize > limits.MaximumPageSize
                || after |> Option.exists (fun cursor -> cursor.View <> view)
            then
                return Error(RecoveryStoreFailure.InvalidInput "recovery list")
            elif
                actorContext.IsNone
                || actorContext.Value.Action <> EndpointAction.RecoveryList
                || actorContext.Value.CaseId.IsSome
            then
                return Error RecoveryStoreFailure.ResourceUnavailable
            else
                try
                    return!
                        listSnapshot
                            dataSource
                            limits
                            view
                            after
                            pageSize
                            actorContext.Value
                            cancellationToken
                with
                | :? OperationCanceledException -> return Error RecoveryStoreFailure.ReadCancelled
                | error -> return Error(fail error)
        }
