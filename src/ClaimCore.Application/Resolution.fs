namespace ClaimCore.Application

open ClaimCore.RecordFormat
open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Authorship permits receipt metadata; current authority still controls snapshot disclosure.
module internal Resolution =
    let resolve
        (services: ServiceContext)
        operation
        (actor: AuthorityView)
        (entry: StoredEntry option)
        (current: StoredCase option)
        =
        let installation = services.InstallationId
        let principal = services.Principal

        match actor.Actor with
        | _ when not (Authority.validActor principal actor) ->
            Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable)
        | Some owner ->
            match entry with
            | Some entry when entry.OperationId <> operation ->
                Verdict.Fail CoreFault.StoreIntegrityError
            | Some entry when entry.ActorId = owner.ActorId ->
                LedgerEntries.validate installation entry
                |> Result.bind (LedgerEntries.receipt principal actor current)
                |> Result.map Verdict.Replay
                |> Result.defaultWith Verdict.Fail
            | _ -> Verdict.Proceed owner.ActorId
        | None -> Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable)

    let run (services: ServiceContext) (request: OperationLookup) token =
        let store = services.Store
        let principal = services.Principal

        let operationId = OperationId.create request.OperationId

        task {
            if operationId.IsNone then
                return
                    Outcome.Rejected(
                        Refusal.invalidInput (
                            InputTarget.OperationId,
                            Violation.OperationIdRequired
                        )
                    )
            else
                let operationId = operationId.Value

                let! result =
                    store.Write(
                        {
                            WriteOperation.Work =
                                (fun tx ->
                                    task {
                                        let! actor, entry, current =
                                            ExecutionFacts.operation tx principal operationId

                                        let! fenced =
                                            ExecutionFacts.fenced
                                                services
                                                tx
                                                actor
                                                operationId
                                                entry

                                        match fenced with
                                        | Error fault -> return Verdict.Fail fault
                                        | Ok fenced ->
                                            match
                                                resolve services operationId actor entry current
                                            with
                                            | Verdict.Proceed actor ->
                                                let key =
                                                    {
                                                        FenceKey.ActorId = actor
                                                        OperationId = operationId
                                                    }

                                                if not fenced then
                                                    let control =
                                                        LedgerEntries.create
                                                            {
                                                                InstallationId =
                                                                    services.InstallationId
                                                                Head = tx.Head
                                                                Instant = tx.ObservedAt
                                                                Nonce = tx.Nonce
                                                                ActorId = actor
                                                                OperationId = operationId
                                                                Subject =
                                                                    EntryTarget.NonAcceptance
                                                                        operationId
                                                                Request = ControlRecord.encode ()
                                                                Snapshot = Array.empty
                                                            }

                                                    do! tx.Append control
                                                    do! tx.Fence key

                                                return Verdict.Proceed()
                                            | other -> return Verdict.map ignore other
                                    })
                            CancellationToken = token
                        }
                    )

                return
                    match result with
                    | Ok verdict ->
                        Verdict.outcome (fun () -> Resolved.NotAccepted) Resolved.Accepted verdict
                    | Error(StoreFailure.Cancelled | StoreFailure.CommitOutcomeUnknown) ->
                        Outcome.Failed CoreFault.StoreUnavailable
                    | Error failure -> Queries.failure failure
        }
