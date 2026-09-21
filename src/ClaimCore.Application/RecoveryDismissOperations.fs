namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks

/// Dismissal is separate from recovery reads because receipt observation and the technical
/// lifecycle mutation have different points of no return.
module internal RecoveryDismissOperations =
    let private invalidFault message : CoreFault =
        {
            Code = FaultCode.RecoveryIntegrityError
            Message = message
            Action = RecommendedAction.StopAndInvestigate
        }

    let private dismissalOutcome (operationId: Guid) (requestSha256: string) =
        function
        | Ok(RecoveryDismissal.Dismissed updated) ->
            TypedProjection.details updated
            |> Result.map RecoveryDismissOutcome.DismissedPreparation
            |> Result.defaultWith RecoveryDismissOutcome.DismissFailed
        | Ok(RecoveryDismissal.AlreadyDismissed updated) ->
            TypedProjection.details updated
            |> Result.map RecoveryDismissOutcome.AlreadyDismissedPreparation
            |> Result.defaultWith RecoveryDismissOutcome.DismissFailed
        | Ok(RecoveryDismissal.ObservedAccepted _) ->
            RecoveryDismissOutcome.DismissRefused(None, RecoverySupport.accepted)
        | Ok(RecoveryDismissal.RevokedTombstone value) ->
            RecoveryDismissOutcome.AlreadyRevoked
                {
                    OperationId = value.OperationId
                    RevokedAt = value.RevokedAt
                    Reason = value.Reason
                }
        | Ok(RecoveryDismissal.SubmissionAlreadyStarted updated) ->
            TypedProjection.details updated
            |> Result.map (fun value ->
                RecoveryDismissOutcome.DismissRefused(Some value, RecoverySupport.started))
            |> Result.defaultWith RecoveryDismissOutcome.DismissFailed
        | Error RecoveryStoreFailure.TechnicalMutationUnknown ->
            RecoveryDismissOutcome.DismissStateUnknown(
                operationId,
                requestSha256,
                TypedProjection.recoveryFault RecoveryStoreFailure.TechnicalMutationUnknown
            )
        | Error RecoveryStoreFailure.CancelledBeforeCommit ->
            RecoveryDismissOutcome.DismissCancelledBeforeAdmission operationId
        | Error RecoveryStoreFailure.IdempotencyConflict ->
            RecoveryDismissOutcome.DismissRefused(None, RecoverySupport.conflict)
        | Error failure ->
            RecoveryDismissOutcome.DismissFailed(TypedProjection.recoveryFault failure)

    let private afterObservation
        (recovery: IRecoveryStore)
        (operationId: Guid)
        (requestSha256: string)
        (cancellationToken: CancellationToken)
        : Task<RecoveryDismissOutcome> =
        if cancellationToken.IsCancellationRequested then
            Task.FromResult(RecoveryDismissOutcome.DismissCancelledBeforeAdmission operationId)
        else
            task {
                let! result = recovery.Dismiss(operationId, requestSha256, cancellationToken)
                return dismissalOutcome operationId requestSha256 result
            }

    let private dismissRetained
        (store: IClaimStore)
        (recovery: IRecoveryStore)
        (operationId: Guid)
        (requestSha256: string)
        (cancellationToken: CancellationToken)
        (retained: RetainedPreparation)
        : Task<RecoveryDismissOutcome> =
        task {
            if retained.RequestSha256 <> requestSha256 then
                return RecoveryDismissOutcome.DismissRefused(None, RecoverySupport.digestMismatch)
            else
                match TypedProjection.details retained with
                | Error fault -> return RecoveryDismissOutcome.DismissFailed fault
                | Ok details ->
                    match! TypedQueries.observe store operationId cancellationToken with
                    | QueryOutcome.Succeeded(Lookup.Found _) ->
                        return
                            RecoveryDismissOutcome.DismissRefused(
                                Some details,
                                RecoverySupport.accepted
                            )
                    | QueryOutcome.Failed fault -> return RecoveryDismissOutcome.DismissFailed fault
                    | QueryOutcome.Rejected _ ->
                        return
                            RecoveryDismissOutcome.DismissFailed(
                                invalidFault "Stored recovery data failed validation."
                            )
                    | QueryOutcome.Cancelled ->
                        return RecoveryDismissOutcome.DismissCancelledBeforeAdmission operationId
                    | QueryOutcome.Succeeded(Lookup.NotFound _) ->
                        return!
                            afterObservation recovery operationId requestSha256 cancellationToken
        }

    let dismiss
        (store: IClaimStore)
        (recovery: IRecoveryStore)
        (operationId: Guid)
        (requestSha256: string)
        (confirmed: bool)
        (cancellationToken: CancellationToken)
        : Task<RecoveryDismissOutcome> =
        if cancellationToken.IsCancellationRequested then
            Task.FromResult(RecoveryDismissOutcome.DismissCancelledBeforeAdmission operationId)
        elif
            operationId = Guid.Empty
            || not (RecoverySupport.validDigest requestSha256)
            || not confirmed
        then
            Task.FromResult(
                RecoveryDismissOutcome.DismissRefused(None, RecoverySupport.invalid "dismiss")
            )
        else
            task {
                match! recovery.Get(operationId, cancellationToken) with
                | Error RecoveryStoreFailure.ReadCancelled ->
                    return RecoveryDismissOutcome.DismissCancelledBeforeAdmission operationId
                | Error failure ->
                    return
                        RecoveryDismissOutcome.DismissFailed(TypedProjection.recoveryFault failure)
                | Ok None -> return RecoveryDismissOutcome.DismissNotFound operationId
                | Ok(Some _) when cancellationToken.IsCancellationRequested ->
                    return RecoveryDismissOutcome.DismissCancelledBeforeAdmission operationId
                | Ok(Some(RecoveryStoredOperation.RevokedTombstone _)) ->
                    return! afterObservation recovery operationId requestSha256 cancellationToken
                | Ok(Some(RecoveryStoredOperation.Retained(retained, _))) ->
                    return!
                        dismissRetained
                            store
                            recovery
                            operationId
                            requestSha256
                            cancellationToken
                            retained
            }
