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
                OperationStorage.find tx.Connection tx.Cipher operation tx.CancellationToken

            let! person = AggregateStorage.principal tx services.Principal

            let caller =
                person
                |> Option.map ClaimCore.Domain.Aggregates.Person.access
                |> AccessView.ofActor

            let target =
                entry
                |> Option.bind (fun stored -> DisclosureReads.receiptTarget stored.Changes.Head)

            let capture outcome =
                match target with
                | Some(caseId, attachment) ->
                    DisclosureReads.observe
                        services.Principal
                        caller
                        DisclosureKind.CaseRead
                        (Some caseId)
                        attachment
                        outcome
                | None -> outcome, None

            if not (Access.validActor services.Principal caller) then
                return capture QueryResults.unavailable
            else
                match entry, caller.Actor with
                | Some entry, Some requester when entry.Root.Header.OperationId = operation ->
                    let own = entry.Root.Header.Authority = Authority.Actor requester.ActorId

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
                        let disclosure =
                            target
                            |> Option.bind (fun (caseId, attachment) ->
                                DisclosureReads.refused
                                    services.Principal
                                    caller
                                    (Some caseId)
                                    attachment)

                        return Outcome.Succeeded None, disclosure
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
                            |> capture
                | Some _, _ -> return Outcome.Failed CoreFault.StoreIntegrityError, None
                | None, _ -> return Outcome.Succeeded None, None
        }

    let private confirm (services: ServiceContext) operation token =
        task {
            let! result =
                services.Store.Write
                    {
                        Work =
                            fun tx ->
                                task {
                                    let! found, disclosure = receipt services tx operation false

                                    match found with
                                    | Outcome.Succeeded(Some accepted) ->
                                        return Verdict.Proceed(accepted, disclosure)
                                    | Outcome.Succeeded None ->
                                        let! frontier = RegisterReads.expiredBefore tx ()

                                        if OperationLifetime.expiredBefore frontier operation then
                                            return
                                                Verdict.Proceed(
                                                    OperationLookupResult.NotAccepted,
                                                    disclosure
                                                )
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
                                                        Verdict.Proceed(
                                                            OperationLookupResult.NotAccepted,
                                                            disclosure
                                                        )
                                                else
                                                    return
                                                        Verdict.Fail CoreFault.StoreIntegrityError
                                            else
                                                return
                                                    Verdict.Proceed(
                                                        OperationLookupResult.Pending(
                                                            OperationLifetime.finalAt operation
                                                        ),
                                                        disclosure
                                                    )
                                    | Outcome.Rejected refusal -> return Verdict.Refuse refusal
                                    | Outcome.Failed fault -> return Verdict.Fail fault
                                }

                        CancellationToken = token
                    }

            return
                match result with
                | Ok(Verdict.Proceed(value, disclosure)) -> Outcome.Succeeded value, disclosure
                | Ok(Verdict.Replay receipt) ->
                    Outcome.Succeeded(OperationLookupResult.Accepted(receipt, true)), None
                | Ok(Verdict.Refuse refusal) -> Outcome.Rejected refusal, None
                | Ok(Verdict.Fail fault) -> Outcome.Failed fault, None
                | Error(StoreFailure.Cancelled | StoreFailure.CommitOutcomeUnknown) ->
                    Outcome.Failed CoreFault.StoreUnavailable, None
                | Error failure -> QueryResults.failure failure, None
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

                let! captured =
                    task {
                        match result with
                        | Error failure -> return QueryResults.failure failure, None
                        | Ok(_, _, (Outcome.Rejected refusal, disclosure)) ->
                            return Outcome.Rejected refusal, disclosure
                        | Ok(_, _, (Outcome.Failed fault, disclosure)) ->
                            return Outcome.Failed fault, disclosure
                        | Ok(_, _, (Outcome.Succeeded(Some accepted), disclosure)) ->
                            return Outcome.Succeeded accepted, disclosure
                        | Ok(_, frontier, (Outcome.Succeeded None, disclosure)) when
                            OperationLifetime.expiredBefore frontier operation
                            ->
                            return Outcome.Succeeded OperationLookupResult.NotAccepted, disclosure
                        | Ok(observedAt, _, (Outcome.Succeeded None, disclosure)) ->
                            if
                                observedAt - OperationId.issuedAt operation >
                                    OperationLifetime.window
                            then
                                // Confirmation publishes the monotonic cutoff before returning final absence.
                                let! outcome, recorded = confirm services operation token
                                return outcome, Option.orElse disclosure recorded
                            else
                                try
                                    return
                                        Outcome.Succeeded(
                                            OperationLookupResult.Pending(
                                                OperationLifetime.finalAt operation
                                            )
                                        ),
                                        disclosure
                                with :? ArgumentOutOfRangeException ->
                                    // The supplied timestamp is beyond a representable deadline; it cannot enter today's window.
                                    return
                                        Outcome.Rejected(
                                            Refusal.OperationIdOutOfWindow(
                                                OperationId.issuedAt operation,
                                                observedAt
                                            )
                                        ),
                                        disclosure
                    }

                return! DisclosureReads.release services token captured
        }
