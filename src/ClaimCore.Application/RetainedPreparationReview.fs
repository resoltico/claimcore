namespace ClaimCore.Application

open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain

[<NoEquality; NoComparison>]
type internal PreviewResult =
    | Previewed of AdvisoryReview
    | PreviewRejected of Rejection
    | PreviewFailed of CoreFault

/// Review and authoritative receipt handling for previously retained technical material.
module internal RetainedPreparationReview =
    let preview
        (store: IClaimStore)
        (clock: IBusinessTime)
        (request: CommandRequest)
        : Task<PreviewResult> =
        task {
            match! store.Get request.CaseReference with
            | Error failure -> return PreviewFailed(TypedProjection.coreFault failure)
            | Ok current ->
                let context = clock.Capture()

                match Claim.decide context.EffectiveBusinessDate request current with
                | Error rejection -> return PreviewRejected(TypedProjection.rejection rejection)
                | Ok proposed ->
                    return Previewed(AdvisoryReviewProjection.create context current proposed)
        }

    let private replayFault: CoreFault =
        {
            Code = FaultCode.RecoveryIntegrityError
            Message = "An exact retained preparation could not be verified."
            Action = RecommendedAction.StopAndInvestigate
        }

    let private observedOutcome
        (store: IClaimStore)
        (clock: IBusinessTime)
        (request: CommandRequest)
        (retained: RetainedPreparation)
        (details: PreparationDetails)
        : Task<PrepareOutcome> =
        task {
            let! verified =
                ObservedReceiptVerification.verify store clock retained details.Summary

            match verified with
            | RetainedResolution.ObservedReceipt receipt ->
                return PrepareOutcome.ObservedAccepted receipt
            | RetainedResolution.ReceiptIdentityConflict ->
                return
                    PrepareOutcome.PrepareRejected(
                        request.OperationId,
                        AcceptedObservation.idempotencyConflict
                    )
            | RetainedResolution.ResolutionCancelledBeforeAttempt _ ->
                return PrepareOutcome.CancelledBeforeAdmission request.OperationId
            | RetainedResolution.ResolutionFailedBeforeAttempt(_, fault) ->
                return PrepareOutcome.PrepareFailed(request.OperationId, fault)
            | _ -> return PrepareOutcome.PrepareFailed(request.OperationId, replayFault)
        }

    let knownRetained
        (store: IClaimStore)
        (clock: IBusinessTime)
        (request: CommandRequest)
        (retained: RetainedPreparation)
        (cancellationToken: CancellationToken)
        : Task<PrepareOutcome> =
        task {
            match TypedProjection.details retained with
            | Error fault -> return PrepareOutcome.PrepareFailed(request.OperationId, fault)
            | Ok details ->
                let! observed = store.Operation request.OperationId

                match observed with
                | Ok(Some _) -> return! observedOutcome store clock request retained details
                | _ when cancellationToken.IsCancellationRequested ->
                    return PrepareOutcome.CancelledBeforeAdmission request.OperationId
                | Error failure ->
                    return
                        PrepareOutcome.PrepareFailed(
                            request.OperationId,
                            TypedProjection.coreFault failure
                        )
                | Ok None ->
                    match! preview store clock request with
                    | Previewed review -> return PrepareOutcome.Prepared(details, review)
                    | PreviewRejected reason ->
                        match!
                            AcceptedObservation.prepare
                                store
                                request.OperationId
                                retained.RequestSha256
                        with
                        | Some outcome -> return outcome
                        | None -> return PrepareOutcome.RetainedForRecovery(details, reason)
                    | PreviewFailed fault ->
                        return PrepareOutcome.PrepareFailed(request.OperationId, fault)
        }
