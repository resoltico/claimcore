namespace ClaimCore.Application

open System
open ClaimCore.Domain

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
            Verdict.Refuse Refusal.ResourceUnavailable
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
        | None -> Verdict.Refuse Refusal.ResourceUnavailable

    let run (services: ServiceContext) (request: ResolveRequest) token =
        let store = services.Store
        let principal = services.Principal

        task {
            if request.OperationId = Guid.Empty then
                return
                    ResolveReply.Rejected(
                        Refusal.InvalidInput(InputTarget.OperationId, Violation.OperationIdRequired)
                    )
            else
                let! result =
                    store.Write(
                        {
                            WriteOperation.Work =
                                (fun tx ->
                                    task {
                                        let! actor, entry, current =
                                            ExecutionFacts.operation
                                                tx
                                                principal
                                                request.OperationId

                                        match
                                            resolve
                                                services
                                                request.OperationId
                                                actor
                                                entry
                                                current
                                        with
                                        | Verdict.Proceed actor ->
                                            do!
                                                tx.Fence(
                                                    {
                                                        FenceKey.ActorId = actor
                                                        OperationId = request.OperationId
                                                    }
                                                )

                                            return Verdict.Proceed()
                                        | other -> return Verdict.map ignore other
                                    })
                            CancellationToken = token
                        }
                    )

                return
                    match result with
                    | Ok(Verdict.Proceed()) -> ResolveReply.NotAccepted
                    | Ok(Verdict.Replay receipt) -> ResolveReply.Accepted receipt
                    | Ok(Verdict.Refuse refusal) -> ResolveReply.Rejected refusal
                    | Ok(Verdict.Fail fault) -> ResolveReply.Failed fault
                    | Error(StoreFailure.Cancelled | StoreFailure.CommitOutcomeUnknown) ->
                        ResolveReply.Failed CoreFault.StoreUnavailable
                    | Error failure -> ResolveReply.Failed(Queries.fault failure)
        }
