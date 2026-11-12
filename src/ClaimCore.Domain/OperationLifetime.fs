namespace ClaimCore.Domain

open System

/// Execution time is the provider instant obtained under the ledger-head lock.
module OperationLifetime =
    [<Literal>]
    let WindowMilliseconds = 600000L

    [<Literal>]
    let FutureAllowanceMilliseconds = 120000L

    let window = TimeSpan.FromMilliseconds(float WindowMilliseconds)
    let futureAllowance = TimeSpan.FromMilliseconds(float FutureAllowanceMilliseconds)
    let finalAt operation = OperationId.issuedAt operation + window

    let admits (observedAt: DateTimeOffset) operation =
        let age = observedAt - OperationId.issuedAt operation
        age <= window && age >= -futureAllowance

    /// Preserve the provider instant's precision; UUID issuance itself has millisecond precision.
    let cutoff (observedAt: DateTimeOffset) =
        if observedAt.UtcTicks < window.Ticks then
            DateTimeOffset.MinValue
        else
            observedAt.ToUniversalTime() - window

    let expiredBefore (frontier: DateTimeOffset option) operation =
        frontier
        |> Option.exists (fun cutoff -> OperationId.issuedAt operation < cutoff)

    /// None means no confirmed cutoff; a later observation can never lower an existing frontier.
    let advanceFrontier (previous: DateTimeOffset option) (candidate: DateTimeOffset) =
        Some(previous |> Option.map (max candidate) |> Option.defaultValue candidate)
        |> Option.map (fun instant -> instant.ToUniversalTime())

    let frontierRefusal frontier observedAt operation =
        if expiredBefore frontier operation then
            frontier
            |> Option.map (fun cutoff -> Refusal.OperationClockUnavailable(cutoff, observedAt))
        else
            None

    let refusal frontier observedAt operation =
        match frontierRefusal frontier observedAt operation with
        | Some refusal -> Some refusal
        | None when not (admits observedAt operation) ->
            Some(Refusal.OperationIdOutOfWindow(OperationId.issuedAt operation, observedAt))
        | None -> None
