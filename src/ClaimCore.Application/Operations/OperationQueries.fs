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
            let! caller = ExecutionEnvironment.requester tx services.Principal

            let capture target outcome =
                DisclosureReads.observe
                    services.Principal
                    caller
                    DisclosureKind.OperationLookup
                    (target |> Option.map fst)
                    (target |> Option.bind snd)
                    outcome

            if not (Access.validActor services.Principal caller) then
                return capture None QueryResults.unavailable
            else
                let! entry =
                    OperationStorage.findStructure
                        tx.Connection
                        tx.InstallationId
                        operation
                        tx.CancellationToken

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
                        return capture None (Outcome.Succeeded None)
                    else
                        match!
                            OperationStorage.find
                                tx.Connection
                                tx.Cipher
                                operation
                                tx.CancellationToken
                        with
                        | Some complete ->
                            let! receipt =
                                ReceiptReads.read
                                    (ServiceContext.basis services tx.ObservedAt)
                                    services.Principal
                                    caller
                                    tx
                                    complete

                            return
                                receipt
                                |> Result.map (fun receipt ->
                                    Some(OperationLookupResult.Accepted(receipt, own)))
                                |> Outcome.ofResult
                                |> capture (DisclosureReads.receiptTarget entry.Changes.Head)
                        | None -> return Outcome.Failed CoreFault.StoreIntegrityError, None
                | Some _, _ -> return Outcome.Failed CoreFault.StoreIntegrityError, None
                | None, _ -> return capture None (Outcome.Succeeded None)
        }

    let private confirm (services: ServiceContext) operation token =
        task {
            let! result =
                services.Store.Write
                    {
                        WriteOperation.RequiresEncryption = false
                        Work =
                            fun tx ->
                                task {
                                    let! found, disclosure = receipt services tx operation false

                                    match found with
                                    | Outcome.Succeeded(Some accepted) ->
                                        return
                                            Verdict.Proceed(Outcome.Succeeded accepted, disclosure)
                                    | Outcome.Succeeded None ->
                                        let! frontier = RegisterReads.expiredBefore tx ()

                                        if OperationLifetime.expiredBefore frontier operation then
                                            return
                                                Verdict.Proceed(
                                                    Outcome.Succeeded
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
                                                            Outcome.Succeeded
                                                                OperationLookupResult.NotAccepted,
                                                            disclosure
                                                        )
                                                else
                                                    return
                                                        Verdict.Fail CoreFault.StoreIntegrityError
                                            else
                                                return
                                                    Verdict.Proceed(
                                                        Outcome.Succeeded(
                                                            OperationLookupResult.Pending(
                                                                OperationLifetime.finalAt operation
                                                            )
                                                        ),
                                                        disclosure
                                                    )
                                    | Outcome.Rejected refusal ->
                                        return Verdict.Proceed(Outcome.Rejected refusal, disclosure)
                                    | Outcome.Failed fault -> return Verdict.Fail fault
                                }

                        CancellationToken = token
                    }

            return
                match result with
                | Ok(Verdict.Proceed(value, disclosure)) -> value, disclosure
                | Ok(Verdict.Replay _) -> Outcome.Failed CoreFault.StoreIntegrityError, None
                | Ok(Verdict.Refuse refusal) -> Outcome.Rejected refusal, None
                | Ok(Verdict.Fail fault) -> Outcome.Failed fault, None
                | Error(StoreFailure.Cancelled | StoreFailure.CommitOutcomeUnknown) ->
                    Outcome.Failed CoreFault.StoreUnavailable, None
                | Error failure -> QueryResults.failure failure, None
        }

    type private LookupPhase<'disclosure> =
        | Resolve of Outcome<OperationLookupResult> * 'disclosure option
        | ConfirmAbsence of OperationId

    let get (services: ServiceContext) (request: OperationLookup) token =
        task {
            match OperationId.create request.OperationId with
            | None ->
                return!
                    RequestRefusals.protectedRead
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

                let phase =
                    match result with
                    | Error failure -> Resolve(QueryResults.failure failure, None)
                    | Ok(_, _, (Outcome.Rejected refusal, disclosure)) ->
                        Resolve(Outcome.Rejected refusal, disclosure)
                    | Ok(_, _, (Outcome.Failed fault, disclosure)) ->
                        Resolve(Outcome.Failed fault, disclosure)
                    | Ok(_, _, (Outcome.Succeeded(Some accepted), disclosure)) ->
                        Resolve(Outcome.Succeeded accepted, disclosure)
                    | Ok(_, frontier, (Outcome.Succeeded None, disclosure)) when
                        OperationLifetime.expiredBefore frontier operation
                        ->
                        Resolve(Outcome.Succeeded OperationLookupResult.NotAccepted, disclosure)
                    | Ok(observedAt, _, (Outcome.Succeeded None, disclosure)) ->
                        if
                            observedAt - OperationId.issuedAt operation > OperationLifetime.window
                        then
                            ConfirmAbsence operation
                        else
                            try
                                Resolve(
                                    Outcome.Succeeded(
                                        OperationLookupResult.Pending(
                                            OperationLifetime.finalAt operation
                                        )
                                    ),
                                    disclosure
                                )
                            with :? ArgumentOutOfRangeException ->
                                Resolve(
                                    Outcome.Rejected(
                                        Refusal.OperationIdOutOfWindow(
                                            OperationId.issuedAt operation,
                                            observedAt
                                        )
                                    ),
                                    disclosure
                                )

                match phase with
                | Resolve(outcome, disclosure) ->
                    return! DisclosureReads.release services token (outcome, disclosure)
                | ConfirmAbsence operation ->
                    // The read scope is complete before confirmation acquires head-write capacity.
                    let! outcome, disclosure = confirm services operation token

                    return! DisclosureReads.release services token (outcome, disclosure)
        }
