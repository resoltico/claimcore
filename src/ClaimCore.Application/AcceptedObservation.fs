namespace ClaimCore.Application

open System.Threading.Tasks

/// Accepted claim history is the authority for exact replay, independent of recovery housekeeping.
module internal AcceptedObservation =
    let idempotencyConflict: Rejection = Rejection.IdempotencyConflict

    let prepare (store: IClaimStore) operationId digest : Task<PrepareOutcome option> =
        task {
            match! store.Accepted(operationId, digest) with
            | Ok None -> return None
            | Ok(Some receipt) ->
                return Some(PrepareOutcome.ObservedAccepted(TypedProjection.receipt receipt))
            | Error CoreFailure.IdempotencyConflict ->
                return Some(PrepareOutcome.PrepareRejected(operationId, idempotencyConflict))
            | Error failure ->
                return
                    Some(
                        PrepareOutcome.PrepareFailed(operationId, TypedProjection.coreFault failure)
                    )
        }
