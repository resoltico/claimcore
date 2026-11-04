namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Every continuation rechecks current authority and current actor names inside its read snapshot.
module internal TimelineQueries =
    open TimelineHistory

    let read (services: ServiceContext) (request: CaseTimelineRequest) token =
        task {
            if not (QueryResults.validLimit request.Limit) then
                return QueryResults.invalidLimit ()
            else
                match CaseExecution.validateSelector request.Target with
                | Error refusal -> return Outcome.Rejected refusal
                | Ok() ->
                    let! result =
                        services.Store.Read
                            {
                                CancellationToken = token
                                Work =
                                    fun tx ->
                                        task {
                                            let! view =
                                                ExecutionFacts.case services tx None request.Target

                                            match view.Case with
                                            | Some parent when
                                                Authority.can
                                                    services.Principal
                                                    view.Actor
                                                    Capability.ReadHistory
                                                    (Scope.Case parent.CaseId)
                                                ->
                                                let scope =
                                                    binding
                                                        parent.CaseId
                                                        view.Actor.Actor.Value.ActorId
                                                        request.Filter

                                                match
                                                    QueryResults.cursor
                                                        (Cursors.decodeTimeline
                                                            services.InstallationId
                                                            scope)
                                                        request.AfterCursor
                                                with
                                                | Error refusal -> return Outcome.Rejected refusal
                                                | Ok position ->
                                                    let before =
                                                        position |> Option.map _.BeforeSequence

                                                    let range = position |> Option.bind _.Range

                                                    let! page =
                                                        tx.TimelinePage
                                                            {
                                                                CaseId = parent.CaseId
                                                                Filter = request.Filter
                                                                Range = range
                                                                BeforeSequence = before
                                                                Limit = request.Limit
                                                            }

                                                    if
                                                        not (
                                                            validPage
                                                                parent.CaseId
                                                                request.Filter
                                                                range
                                                                before
                                                                request.Limit
                                                                page
                                                        )
                                                    then
                                                        return
                                                            Outcome.Failed
                                                                CoreFault.StoreIntegrityError
                                                    else
                                                        let erased =
                                                            CaseState.lifecycle
                                                                parent.Snapshot.State
                                                                =
                                                                Lifecycle.Erased

                                                        let full =
                                                            Authority.can
                                                                services.Principal
                                                                view.Actor
                                                                Capability.ReadHistoryFull
                                                                (Scope.Case parent.CaseId)

                                                        let! items =
                                                            entries
                                                                services
                                                                tx
                                                                erased
                                                                full
                                                                range
                                                                page

                                                        return
                                                            items
                                                            |> Result.map (fun items ->
                                                                {
                                                                    CaseTimelinePage.Basis =
                                                                        ServiceContext.basis
                                                                            services
                                                                            tx.ObservedAt
                                                                    CaseId = parent.CaseId
                                                                    Reference =
                                                                        parent.Snapshot.Reference
                                                                    Items = items
                                                                    NextCursor =
                                                                        page.Next
                                                                        |> Option.map (
                                                                            Cursors.encodeTimeline
                                                                                services
                                                                                    .InstallationId
                                                                                scope
                                                                                range
                                                                        )
                                                                })
                                                            |> Result.map Outcome.Succeeded
                                                            |> Result.defaultWith Outcome.Failed
                                            | _ -> return QueryResults.unavailable
                                        }
                            }

                    return QueryResults.readResult result
        }
