namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.RecordFormat

module internal RecoveryReadOperations =
    let private projectListItem =
        function
        | RecoveryStoreListItem.Retained(preparation, authority) ->
            TypedProjection.summaryWithKnownAuthority false authority preparation
            |> Result.map RetainedRecoveryItem
        | RecoveryStoreListItem.Revoked revocation ->
            TypedProjection.revokedOperation revocation |> RevokedRecoveryItem |> Ok

    let private projectPage (page: RecoveryStorePage) =
        let items = page.Items |> List.map projectListItem

        match
            items
            |> List.tryPick (function
                | Error fault -> Some fault
                | Ok _ -> None)
        with
        | Some fault -> RecoveryQueryOutcome.RecoveryFailed fault
        | None ->
            RecoveryQueryOutcome.RecoverySucceeded
                {
                    View = page.View
                    Items = items |> List.choose Result.toOption
                    NextCursor = page.NextAfter |> Option.map RecoveryCursorCodec.encode
                    PendingPreparationCount = page.PendingPreparationCount
                    PendingCanonicalRequestBytes = page.PendingCanonicalRequestBytes
                    MaximumPendingPreparations = page.MaximumPendingPreparations
                    MaximumPendingCanonicalRequestBytes = page.MaximumPendingCanonicalRequestBytes
                    NearCapacity =
                        page.PendingPreparationCount >= (page.MaximumPendingPreparations * 9 / 10)
                        || page.PendingCanonicalRequestBytes
                           >= (page.MaximumPendingCanonicalRequestBytes * 9L / 10L)
                }

    let list
        (recovery: IRecoveryStore)
        (view: RecoveryListView)
        (afterCursor: string option)
        (limit: int)
        (cancellationToken: CancellationToken)
        : Task<RecoveryQueryOutcome<RecoveryPage>> =
        if cancellationToken.IsCancellationRequested then
            Task.FromResult(RecoveryQueryOutcome.RecoveryCancelled)
        elif limit < 1 || limit > SemanticContract.current.MaximumPageSize then
            Task.FromResult(
                RecoveryQueryOutcome.RecoveryRejected(RecoveryRejection.PageLimitOutOfRange)
            )
        else
            let after =
                afterCursor
                |> Option.map (RecoveryCursorCodec.decode >> Result.map Some)
                |> Option.defaultValue (Ok None)

            match after with
            | Error _ ->
                Task.FromResult(
                    RecoveryQueryOutcome.RecoveryRejected(RecoveryRejection.ListCursorInvalid)
                )
            | Ok cursor when cursor |> Option.exists (fun value -> value.View <> view) ->
                Task.FromResult(
                    RecoveryQueryOutcome.RecoveryRejected(RecoveryRejection.ListCursorViewMismatch)
                )
            | Ok cursor ->
                task {
                    match! recovery.List(view, cursor, limit, cancellationToken) with
                    | Error RecoveryStoreFailure.ReadCancelled ->
                        return RecoveryQueryOutcome.RecoveryCancelled
                    | Error failure ->
                        return
                            RecoveryQueryOutcome.RecoveryFailed(
                                TypedProjection.recoveryFault failure
                            )
                    | Ok page ->
                        if cancellationToken.IsCancellationRequested then
                            return RecoveryQueryOutcome.RecoveryCancelled
                        else
                            return projectPage page
                }

    let private inspectRetained
        (store: IClaimStore)
        (operationId: Guid)
        (cancellationToken: CancellationToken)
        (retained: RetainedPreparation)
        (attempts: RecoveryAttemptPage)
        (authority: RecoveryAuthority)
        : Task<RecoveryQueryOutcome<Lookup<RecoveryInspection, Guid>>> =
        task {
            let attemptPage =
                {
                    Items = attempts.Items
                    NextCursor = attempts.NextAfter |> Option.map RecoveryAttemptCursorCodec.encode
                }

            match TypedProjection.detailsWithKnownAuthority retained attemptPage authority with
            | Error fault -> return RecoveryQueryOutcome.RecoveryFailed fault
            | Ok details ->
                match! TypedQueries.observe store operationId cancellationToken with
                | QueryOutcome.Succeeded observation ->
                    if cancellationToken.IsCancellationRequested then
                        return RecoveryQueryOutcome.RecoveryCancelled
                    else
                        let inspected =
                            match observation with
                            | Lookup.Found _ -> TypedProjection.acceptedDetails details
                            | Lookup.NotFound _ -> details

                        return
                            RecoveryQueryOutcome.RecoverySucceeded(
                                Lookup.Found(
                                    RecoveryInspection.RetainedInspection
                                        {
                                            Preparation = inspected
                                            Observation = observation
                                        }
                                )
                            )
                | QueryOutcome.Failed fault -> return RecoveryQueryOutcome.RecoveryFailed fault
                | QueryOutcome.Cancelled -> return RecoveryQueryOutcome.RecoveryCancelled
                | QueryOutcome.Rejected _ ->
                    return RecoveryQueryOutcome.RecoveryFailed(CoreFault.StoredRecoveryInvalid)
        }

    let private inspectCursor operationId afterCursor =
        afterCursor
        |> Option.map (RecoveryAttemptCursorCodec.decode >> Result.map Some)
        |> Option.defaultValue (Ok None)
        |> Result.mapError (fun _ -> RecoveryRejection.AttemptCursorInvalid)
        |> Result.bind (fun cursor ->
            if cursor |> Option.exists (fun value -> value.OperationId <> operationId) then
                Error RecoveryRejection.AttemptCursorOperationMismatch
            else
                Ok cursor)

    let private inspectStored
        (store: IClaimStore)
        (operationId: Guid)
        (cancellationToken: CancellationToken)
        =
        function
        | None ->
            Task.FromResult(RecoveryQueryOutcome.RecoverySucceeded(Lookup.NotFound operationId))
        | Some(RecoveryStoreInspection.Retained(retained, attempts, authority)) ->
            inspectRetained store operationId cancellationToken retained attempts authority
        | Some(RecoveryStoreInspection.RevokedTombstone revocation) ->
            RecoveryInspection.RevokedInspection(TypedProjection.revokedOperation revocation)
            |> Lookup.Found
            |> RecoveryQueryOutcome.RecoverySucceeded
            |> Task.FromResult

    let private readInspection
        (store: IClaimStore)
        (recovery: IRecoveryStore)
        (operationId: Guid)
        (cursor: RecoveryAttemptCursor option)
        (limit: int)
        (cancellationToken: CancellationToken)
        : Task<RecoveryQueryOutcome<Lookup<RecoveryInspection, Guid>>> =
        task {
            match! recovery.Inspect(operationId, cursor, limit, cancellationToken) with
            | Error RecoveryStoreFailure.ReadCancelled ->
                return RecoveryQueryOutcome.RecoveryCancelled
            | Error failure ->
                return RecoveryQueryOutcome.RecoveryFailed(TypedProjection.recoveryFault failure)
            | Ok inspected -> return! inspectStored store operationId cancellationToken inspected
        }

    let inspect
        (store: IClaimStore)
        (recovery: IRecoveryStore)
        (operationId: Guid)
        (afterCursor: string option)
        (limit: int)
        (cancellationToken: CancellationToken)
        : Task<RecoveryQueryOutcome<Lookup<RecoveryInspection, Guid>>> =
        if cancellationToken.IsCancellationRequested then
            Task.FromResult(RecoveryQueryOutcome.RecoveryCancelled)
        elif operationId = Guid.Empty then
            Task.FromResult(
                RecoveryQueryOutcome.RecoveryRejected RecoveryRejection.OperationIdRequired
            )
        elif limit < 1 || limit > SemanticContract.current.MaximumPageSize then
            Task.FromResult(
                RecoveryQueryOutcome.RecoveryRejected RecoveryRejection.PageLimitOutOfRange
            )
        else
            match inspectCursor operationId afterCursor with
            | Error rejection -> Task.FromResult(RecoveryQueryOutcome.RecoveryRejected rejection)
            | Ok cursor -> readInspection store recovery operationId cursor limit cancellationToken

    let resolve
        (store: IClaimStore)
        (recovery: IRecoveryStore)
        (clock: IBusinessTime)
        (operationId: Guid)
        (requestSha256: string)
        (cancellationToken: CancellationToken)
        : Task<ResolveOutcome> =
        if operationId = Guid.Empty then
            Task.FromResult(
                ResolveOutcome.RefusedBeforeAttempt(None, RecoveryRejection.OperationIdRequired)
            )
        elif not (RecoverySupport.validDigest requestSha256) then
            Task.FromResult(
                ResolveOutcome.RefusedBeforeAttempt(None, RecoveryRejection.RequestDigestInvalid)
            )
        elif cancellationToken.IsCancellationRequested then
            Task.FromResult(ResolveOutcome.ResolveCancelledBeforeAdmission operationId)
        else
            task {
                match! store.Accepted(operationId, requestSha256) with
                | Ok(Some receipt) ->
                    return ResolveOutcome.ResolveObservedAccepted(TypedProjection.receipt receipt)
                | Error CoreFailure.IdempotencyConflict ->
                    return ResolveOutcome.RefusedBeforeAttempt(None, RecoverySupport.conflict)
                | Error failure ->
                    return
                        ResolveOutcome.ResolveFailedBeforeAttempt(
                            None,
                            TypedProjection.coreFault failure
                        )
                | Ok None ->
                    let! result =
                        TypedResolution.resolveRetained
                            store
                            recovery
                            clock
                            operationId
                            requestSha256
                            cancellationToken

                    return RecoverySupport.resolveOutcome result
            }
