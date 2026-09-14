namespace ClaimCore.Application

open System.Threading.Tasks
open ClaimCore.Domain

/// Executes a validated command inside the storage transaction; not a renderer-facing seam.
module internal Service =
    let executeAsync
        (store: IClaimStore)
        (clock: IBusinessDate)
        (request: CommandRequest)
        : Task<Result<Receipt, CoreFailure>> =
        match Operation.prepare request with
        | Error error -> Task.FromResult(Error(CoreFailure.Domain error))
        | Ok operation ->
            // Exact successful replay is resolved before consulting the current business date.
            store.Transact(operation, fun current -> Claim.decide (clock.Today()) request current)
