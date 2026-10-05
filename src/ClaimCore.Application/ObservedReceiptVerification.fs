namespace ClaimCore.Application

open System.Threading.Tasks
open ClaimCore.RecordFormat

/// A same-ID receipt is not an exact replay until the accepted fingerprint is checked against the
/// retained canonical bytes by content-bound witnessed observation; this path cannot execute.
module internal ObservedReceiptVerification =
    let private corruptFault: CoreFault = CoreFault.RetainedCanonicalInvalid

    let private identity (preparation: RetainedPreparation) =
        RequestRecord.decode SemanticContract.current.RequestByteLimit preparation.CanonicalRequest
        |> Result.mapError ignore
        |> Result.bind (fun request -> Operation.prepare request |> Result.mapError ignore)
        |> Result.bind (fun operation ->
            if
                (Operation.request operation).OperationId = preparation.OperationId
                && Operation.canonicalRequest operation = preparation.CanonicalRequest
                && Operation.fingerprint operation = preparation.RequestSha256
            then
                Ok operation
            else
                Error())

    let verify
        (store: IClaimStore)
        (preparation: RetainedPreparation)
        (summary: PreparationSummary)
        cancellationToken
        : Task<RetainedResolution> =
        task {
            match identity preparation with
            | Error _ -> return ResolutionFailedBeforeAttempt(Some summary, corruptFault)
            | Ok operation ->
                try
                    let! result =
                        store.Accepted(
                            (Operation.request operation).OperationId,
                            Operation.fingerprint operation,
                            cancellationToken
                        )

                    match result with
                    | Ok(Some accepted) -> return ObservedReceipt(TypedProjection.receipt accepted)
                    | Ok None ->
                        return
                            ResolutionFailedBeforeAttempt(Some summary, CoreFault.StoreUnavailable)
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
