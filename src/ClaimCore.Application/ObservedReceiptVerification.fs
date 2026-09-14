namespace ClaimCore.Application

open System.Threading.Tasks
open ClaimCore.RecordFormat

/// A same-ID receipt is not an exact replay until the accepted fingerprint is checked against the
/// retained canonical bytes by the content-bound claim transaction.
module internal ObservedReceiptVerification =
    let private corruptFault: CoreFault =
        {
            Code = FaultCode.RecoveryIntegrityError
            Message = "Retained canonical request bytes failed integrity validation."
            Action = RecommendedAction.StopAndInvestigate
        }

    let verify
        (store: IClaimStore)
        (clock: IBusinessTime)
        (preparation: RetainedPreparation)
        (summary: PreparationSummary)
        : Task<RetainedResolution> =
        task {
            match
                RequestRecord.decode
                    SemanticContract.current.RequestByteLimit
                    preparation.CanonicalRequest
            with
            | Error _ -> return ResolutionFailedBeforeAttempt(Some summary, corruptFault)
            | Ok request ->
                try
                    let! result = Service.executeAsync store clock request

                    match result with
                    | Ok accepted -> return ObservedReceipt(TypedProjection.receipt accepted)
                    | Error CoreFailure.IdempotencyConflict -> return ReceiptIdentityConflict
                    | Error failure ->
                        return
                            ResolutionFailedBeforeAttempt(
                                Some summary,
                                TypedProjection.coreFault failure
                            )
                with _ ->
                    return
                        ResolutionFailedBeforeAttempt(
                            Some summary,
                            TypedProjection.coreFault CoreFailure.StoreUnavailable
                        )
        }
