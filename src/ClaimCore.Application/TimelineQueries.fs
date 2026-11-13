namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Every continuation rechecks current people and current actor names inside its read snapshot.
module internal TimelineQueries =
    open TimelineHistory

    let read (services: ServiceContext) (request: CaseTimelineRequest) token =
        let pageLimit = min request.Limit Paging.MaximumTimelinePageSize

        task {
            if not (QueryResults.validLimit request.Limit) then
                return! RequestRefusals.invalidLimit services token
            else
                match CaseExecution.validateSelector request.Target with
                | Error refusal -> return! RequestRefusals.read services token refusal
                | Ok() ->
                    let! result =
                        services.Store.Read
                            {
                                CancellationToken = token
                                Work =
                                    fun tx ->
                                        task {
                                            let! view =
                                                ExecutionFacts.case
                                                    services
                                                    tx
                                                    None
                                                    request.Target
                                                    None

                                            match view.Case with
                                            | Some parent when
                                                Access.can
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
                                                        position |> Option.map _.BeforePosition

                                                    let range = position |> Option.bind _.Range

                                                    let! page =
                                                        tx.TimelinePage
                                                            {
                                                                CaseId = parent.CaseId
                                                                Filter = request.Filter
                                                                Range = range
                                                                BeforePosition = before
                                                                Limit = pageLimit
                                                            }

                                                    if
                                                        not (
                                                            validPage
                                                                parent.CaseId
                                                                request.Filter
                                                                range
                                                                before
                                                                pageLimit
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
                                                            Access.can
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
                                                                    CaseTimelinePage.Disclosure =
                                                                        HistoryDisclosure.ofAccess
                                                                            erased
                                                                            full
                                                                    Basis =
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
