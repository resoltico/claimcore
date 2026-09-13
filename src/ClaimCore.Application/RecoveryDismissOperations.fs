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
                match! recovery.Dismiss(operationId, cancellationToken) with
                | Ok(RecoveryDismissal.Dismissed updated) ->
                    return
                        TypedProjection.details updated
                        |> Result.map RecoveryDismissOutcome.DismissedPreparation
                        |> Result.defaultWith RecoveryDismissOutcome.DismissFailed
                | Ok(RecoveryDismissal.AlreadyDismissed updated) ->
                    return
                        TypedProjection.details updated
                        |> Result.map RecoveryDismissOutcome.AlreadyDismissedPreparation
                        |> Result.defaultWith RecoveryDismissOutcome.DismissFailed
                | Ok(RecoveryDismissal.SubmissionAlreadyStarted updated) ->
                    return
                        TypedProjection.details updated
                        |> Result.map (fun value ->
                            RecoveryDismissOutcome.DismissRefused(
                                Some value,
                                RecoverySupport.started
                            ))
                        |> Result.defaultWith RecoveryDismissOutcome.DismissFailed
                | Error RecoveryStoreFailure.TechnicalMutationUnknown ->
                    return
                        RecoveryDismissOutcome.DismissStateUnknown(
                            operationId,
                            requestSha256,
                            TypedProjection.recoveryFault
                                RecoveryStoreFailure.TechnicalMutationUnknown
                        )
                | Error RecoveryStoreFailure.CancelledBeforeCommit ->
                    return RecoveryDismissOutcome.DismissCancelledBeforeAdmission operationId
                | Error failure ->
                    return
                        RecoveryDismissOutcome.DismissFailed(TypedProjection.recoveryFault failure)
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
            match TypedProjection.details retained with
            | Error fault -> return RecoveryDismissOutcome.DismissFailed fault
            | Ok details when retained.RequestSha256 <> requestSha256 ->
                return
                    RecoveryDismissOutcome.DismissRefused(
                        Some details,
                        RecoverySupport.digestMismatch
                    )
            | Ok details ->
                match! TypedQueries.observe store operationId cancellationToken with
                | QueryOutcome.Succeeded(Lookup.Found _) ->
                    return
                        RecoveryDismissOutcome.DismissRefused(
                            Some details,
                            RecoverySupport.accepted
                        )
                | QueryOutcome.Failed fault -> return RecoveryDismissOutcome.DismissFailed fault
                | QueryOutcome.Rejected rejection ->
                    return RecoveryDismissOutcome.DismissFailed(invalidFault rejection.Message)
                | QueryOutcome.Cancelled ->
                    return RecoveryDismissOutcome.DismissCancelledBeforeAdmission operationId
                | QueryOutcome.Succeeded(Lookup.NotFound _) ->
                    return! afterObservation recovery operationId requestSha256 cancellationToken
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
                | Ok(Some retained) ->
                    return!
                        dismissRetained
                            store
                            recovery
                            operationId
                            requestSha256
                            cancellationToken
                            retained
            }
