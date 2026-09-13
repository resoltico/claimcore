namespace ClaimCore.Application

open System
open System.Threading.Tasks
open ClaimCore.Domain

/// Internal application operations behind IClaimsCore; never a renderer-facing persistence seam.
/// V0.1 is a trusted local-operator application, not a multi-tenant authorisation service.
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

    let getAsync (store: IClaimStore) reference : Task<Result<Claim option, CoreFailure>> =
        match Claim.validateReference reference with
        | Error error -> Task.FromResult(Error(CoreFailure.Domain error))
        | Ok() -> store.Get(reference)

    let listAsync (store: IClaimStore) after : Task<Result<CasePage, CoreFailure>> =
        let validation =
            after |> Option.map Claim.validateReference |> Option.defaultValue (Ok())

        match validation with
        | Error error -> Task.FromResult(Error(CoreFailure.Domain error))
        | Ok() -> store.List(after)

    let historyAsync
        (store: IClaimStore)
        reference
        afterVersion
        : Task<Result<HistoryPage, CoreFailure>> =
        match Claim.validateReference reference with
        | Error error -> Task.FromResult(Error(CoreFailure.Domain error))
        | Ok() when afterVersion < 0L ->
            Task.FromResult(
                Error(
                    CoreFailure.Domain(
                        DomainError.InvalidInput("afterVersion", "Use a non-negative version.")
                    )
                )
            )
        | Ok() ->
            task {
                // An empty page is meaningful only for an existing case.  Checking the
                // authoritative current row first prevents a missing reference from being
                // silently projected as a successful empty history.
                match! store.Get(reference) with
                | Error failure -> return Error failure
                | Ok None -> return Error(CoreFailure.Domain DomainError.NotFound)
                | Ok(Some _) -> return! store.History(reference, afterVersion)
            }

    let operationAsync
        (store: IClaimStore)
        operationId
        : Task<Result<Receipt option, CoreFailure>> =
        if operationId = Guid.Empty then
            Task.FromResult(
                Error(
                    CoreFailure.Domain(
                        DomainError.InvalidInput("operationId", "Use a non-empty UUID.")
                    )
                )
            )
        else
            store.Operation(operationId)
