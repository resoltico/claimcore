namespace ClaimCore.TestSupport

open System
open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.Domain

/// Test setup and in-memory mutation are not a production persistence port. PostgreSQL fixtures
/// implement this by retaining, admitting and executing through the actual recovery protocol.
type internal ITestCommandExecutor =
    abstract Execute:
        PreparedOperation *
        (unit -> BusinessContext) *
        (DateOnly -> Claim option -> Result<Claim, DomainError>) ->
            Task<Result<Receipt, CoreFailure>>

module internal CommandExecution =
    let executeAsync (store: IClaimStore) (clock: IBusinessTime) request =
        match Operation.prepare request with
        | Error error -> Task.FromResult(Error(CoreFailure.Domain error))
        | Ok operation ->
            (store :?> ITestCommandExecutor)
                .Execute(
                    operation,
                    clock.Capture,
                    (fun today current -> Claim.decide today request current)
                )
