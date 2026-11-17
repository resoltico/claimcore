namespace ClaimCore.Application.Reads

open ClaimCore.Application.Storage

open ClaimCore.Application.Execution

open ClaimCore.Application

open System
open ClaimCore.RecordFormat
open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Absence becomes final only after confirmation under the same head lock as execution.
module internal OperationQueries =
    let private receipt (services: ServiceContext) (tx: ReadTx) operation discloseForeign =
        task {
            let! entry =
                OperationStorage.find tx.Connection tx.InstallationId operation tx.CancellationToken

            let! person = AggregateStorage.principal tx services.Principal

            let caller =
                person
                |> Option.map ClaimCore.Domain.Aggregates.Person.access
                |> AccessView.ofActor

            if not (Access.validActor services.Principal caller) then
                return QueryResults.unavailable
            else
                match entry, caller.Actor with
                | Some entry, Some requester when entry.Root.Header.OperationId = operation ->
                    let own = entry.Root.Header.ActorId = requester.ActorId

                    if
                        not own
                        && (not discloseForeign
                            || not (
                                Engine.canObserve
                                    services.Principal
                                    caller
                                    entry.Changes.Head.Aggregate
                            ))
                    then
                        return Outcome.Succeeded None
                    else
                        let! receipt =
                            ReceiptReads.read
                                (ServiceContext.basis services tx.ObservedAt)
                                services.Principal
                                caller
                                tx
                                entry

                        return
                            receipt
                            |> Result.map (fun receipt ->
                                Some(OperationLookupResult.Accepted(receipt, own)))
                            |> Outcome.ofResult
                | Some _, _ -> return Outcome.Failed CoreFault.StoreIntegrityError
                | None, _ -> return Outcome.Succeeded None
        }

    let private confirm (services: ServiceContext) operation token =
        task {
            let! result =
                services.Store.Write
                    {
                        Work =
                            fun tx ->
                                task {
                                    let! found = receipt services tx operation false

                                    match found with
                                    | Outcome.Succeeded(Some accepted) ->
                                        return Verdict.Proceed accepted
                                    | Outcome.Succeeded None ->
                                        let! frontier = RegisterReads.expiredBefore tx ()

                                        if OperationLifetime.expiredBefore frontier operation then
                                            return Verdict.Proceed OperationLookupResult.NotAccepted
                                        else
                                            let cutoff = OperationLifetime.cutoff tx.ObservedAt

                                            if OperationId.issuedAt operation < cutoff then
                                                let! committed = tx.AdvanceExpiryFrontier cutoff

                                                if
                                                    OperationLifetime.expiredBefore
                                                        (Some committed)
                                                        operation
                                                then
                                                    return
                                                        Verdict.Proceed
                                                            OperationLookupResult.NotAccepted
                                                else
                                                    return
                                                        Verdict.Fail CoreFault.StoreIntegrityError
                                            else
                                                return
                                                    Verdict.Proceed(
                                                        OperationLookupResult.Pending(
                                                            OperationLifetime.finalAt operation
                                                        )
                                                    )
                                    | Outcome.Rejected refusal -> return Verdict.Refuse refusal
                                    | Outcome.Failed fault -> return Verdict.Fail fault
                                }

                        CancellationToken = token
                    }

            return
                match result with
                | Ok verdict ->
                    Verdict.outcome
                        id
                        (fun receipt -> OperationLookupResult.Accepted(receipt, true))
                        verdict
                | Error(StoreFailure.Cancelled | StoreFailure.CommitOutcomeUnknown) ->
                    Outcome.Failed CoreFault.StoreUnavailable
                | Error failure -> QueryResults.failure failure
        }

    let get (services: ServiceContext) (request: OperationLookup) token =
        task {
            match OperationId.create request.OperationId with
            | None ->
                return!
                    RequestRefusals.read
                        services
                        token
                        (Refusal.invalidInput (
                            InputTarget.OperationId,
                            Violation.OperationIdRequired
                        ))
            | Some operation ->
                let! result =
                    services.Store.Read
                        {
                            Work =
                                fun tx ->
                                    task {
                                        let! found = receipt services tx operation true
                                        let! frontier = RegisterReads.expiredBefore tx ()
                                        return tx.ObservedAt, frontier, found
                                    }
                            CancellationToken = token
                        }

                match result with
                | Error failure -> return QueryResults.failure failure
                | Ok(_, _, Outcome.Rejected refusal) -> return Outcome.Rejected refusal
                | Ok(_, _, Outcome.Failed fault) -> return Outcome.Failed fault
                | Ok(_, _, Outcome.Succeeded(Some accepted)) -> return Outcome.Succeeded accepted
                | Ok(_, frontier, Outcome.Succeeded None) when
                    OperationLifetime.expiredBefore frontier operation
                    ->
                    return Outcome.Succeeded OperationLookupResult.NotAccepted
                | Ok(observedAt, _, Outcome.Succeeded None) ->
                    if observedAt - OperationId.issuedAt operation > OperationLifetime.window then
                        // Confirmation publishes the monotonic cutoff before returning final absence.
                        return! confirm services operation token
                    else
                        try
                            return
                                Outcome.Succeeded(
                                    OperationLookupResult.Pending(
                                        OperationLifetime.finalAt operation
                                    )
                                )
                        with :? ArgumentOutOfRangeException ->
                            // The supplied timestamp is beyond a representable deadline; it cannot enter today's window.
                            return
                                Outcome.Rejected(
                                    Refusal.OperationIdOutOfWindow(
                                        OperationId.issuedAt operation,
                                        observedAt
                                    )
                                )
        }
