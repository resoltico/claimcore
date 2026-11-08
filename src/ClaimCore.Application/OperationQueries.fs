namespace ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Absence becomes final only after confirmation under the same head lock as execution.
module internal OperationQueries =
    let private receipt (services: ServiceContext) (tx: IReadTx) operation discloseForeign =
        task {
            let! entry = tx.FindOperation operation
            let! caller = ExecutionFacts.authority tx services.Principal []

            match caller.Actor with
            | _ when not (Authority.validActor services.Principal caller) ->
                return QueryResults.unavailable
            | None -> return QueryResults.unavailable
            | Some requester ->
                match entry with
                | Some entry when entry.OperationId <> operation ->
                    return Outcome.Failed CoreFault.StoreIntegrityError
                | Some entry when entry.ActorId = requester.ActorId || discloseForeign ->
                    let! actor = ExecutionFacts.operationActor tx services.Principal (Some entry)
                    let own = entry.ActorId = requester.ActorId

                    if not (Authority.validActor services.Principal actor) then
                        if own then
                            return QueryResults.unavailable
                        else
                            return Outcome.Succeeded None
                    elif
                        not own
                        && not (LedgerEntries.canObserve services.Principal actor entry.Subject)
                    then
                        return Outcome.Succeeded None
                    else
                        // Do not read or decode a hidden foreign case's content to establish own absence.
                        let! current = ExecutionFacts.receiptCurrent tx (Some entry)
                        let! authors = tx.ActorRefs [ entry.ActorId ]
                        let! named = ReceiptReads.existing services tx actor current (Some entry)

                        return
                            named
                            |> Result.bind (fun current ->
                                LedgerEntries.validateOperation services.InstallationId entry
                                |> Result.bind (
                                    LedgerEntries.receiptOperation
                                        (ServiceContext.basis services tx.ObservedAt)
                                        services.Principal
                                        actor
                                        (Map.tryFind entry.ActorId authors)
                                        current
                                ))
                            |> Result.map (fun receipt ->
                                Some(OperationLookupResult.Accepted(receipt, own)))
                            |> Outcome.ofResult
                | _ -> return Outcome.Succeeded None
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

                                    return
                                        match found with
                                        | Outcome.Succeeded(Some accepted) ->
                                            Verdict.Proceed accepted
                                        | Outcome.Succeeded None ->
                                            // The lock's instant, rather than an earlier snapshot, proves expiry.
                                            if
                                                tx.ObservedAt - OperationId.issuedAt operation >
                                                    OperationLifetime.window
                                            then
                                                Verdict.Proceed OperationLookupResult.NotAccepted
                                            else
                                                Verdict.Proceed(
                                                    OperationLookupResult.Pending(
                                                        OperationLifetime.finalAt operation
                                                    )
                                                )
                                        | Outcome.Rejected refusal -> Verdict.Refuse refusal
                                        | Outcome.Failed fault -> Verdict.Fail fault
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
                                        return tx.ObservedAt, found
                                    }
                            CancellationToken = token
                        }

                match result with
                | Error failure -> return QueryResults.failure failure
                | Ok(_, Outcome.Rejected refusal) -> return Outcome.Rejected refusal
                | Ok(_, Outcome.Failed fault) -> return Outcome.Failed fault
                | Ok(_, Outcome.Succeeded(Some accepted)) -> return Outcome.Succeeded accepted
                | Ok(observedAt, Outcome.Succeeded None) ->
                    if observedAt - OperationId.issuedAt operation > OperationLifetime.window then
                        // No append and no maintenance guard: this transaction only confirms the locked snapshot.
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
