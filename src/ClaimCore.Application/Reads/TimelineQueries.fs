namespace ClaimCore.Application.Reads

open ClaimCore.Application.Storage

open ClaimCore.Application.Execution

open ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Every continuation rechecks current access and current actor names inside its read snapshot.
module internal TimelineQueries =
    open TimelineHistory

    let private readInSnapshot
        (services: ServiceContext)
        (request: CaseTimelineRequest)
        pageLimit
        (tx: ReadTx)
        (view: CaseReadFacts)
        =
        task {
            match view.Case with
            | Some parent when
                Access.can
                    services.Principal
                    view.Actor
                    Capability.ReadHistory
                    (Scope.Case parent.CaseId)
                ->
                let scope = binding parent.CaseId view.Actor.Actor.Value.ActorId request.Filter

                match
                    QueryResults.cursor
                        (Cursors.decodeTimeline services.InstallationId scope)
                        request.AfterCursor
                with
                | Error refusal -> return Outcome.Rejected refusal
                | Ok position ->
                    let before = position |> Option.map _.BeforePosition

                    let range = position |> Option.bind _.Range

                    let! page =
                        LedgerReads.timelinePage
                            tx
                            {
                                CaseId = parent.CaseId
                                Filter = request.Filter
                                Range = range
                                BeforePosition = before
                                Limit = pageLimit
                            }

                    if not (validPage parent.CaseId request.Filter range before pageLimit page) then
                        return Outcome.Failed CoreFault.StoreIntegrityError
                    else
                        let erased = CaseState.lifecycle parent.Snapshot.State = Lifecycle.Erased

                        let full =
                            Access.can
                                services.Principal
                                view.Actor
                                Capability.ReadHistoryFull
                                (Scope.Case parent.CaseId)

                        let! attachmentDisclosure = AttachmentReads.disclosure tx parent.CaseId

                        let! items =
                            entries services tx parent.Snapshot.Reference erased full range page

                        return
                            items
                            |> Result.map (fun items ->
                                {
                                    CaseTimelinePage.Disclosure =
                                        HistoryDisclosure.ofAccess erased full
                                    AttachmentDisclosure = attachmentDisclosure
                                    Basis = ServiceContext.basis services tx.ObservedAt
                                    CaseId = parent.CaseId
                                    Reference = parent.Snapshot.Reference
                                    Items = items
                                    NextCursor =
                                        page.Next
                                        |> Option.map (
                                            Cursors.encodeTimeline
                                                services.InstallationId
                                                scope
                                                range
                                        )
                                })
                            |> Result.map Outcome.Succeeded
                            |> Result.defaultWith Outcome.Failed
            | _ -> return QueryResults.unavailable
        }

    let private readWith
        audit
        finalize
        reject
        (services: ServiceContext)
        (request: CaseTimelineRequest)
        token
        =
        let pageLimit = min request.Limit Paging.MaximumTimelinePageSize

        task {
            if not (QueryResults.validLimit request.Limit) then
                return! reject services token QueryResults.invalidLimitRefusal
            else
                match CaseRecords.validateSelector request.Target with
                | Error refusal -> return! reject services token refusal
                | Ok() ->
                    let! result =
                        services.Store.Read
                            {
                                CancellationToken = token
                                Work =
                                    fun tx ->
                                        task {
                                            let! view =
                                                CaseReadContext.load services tx request.Target

                                            let! outcome =
                                                readInSnapshot services request pageLimit tx view

                                            if not audit then
                                                return outcome, None
                                            else
                                                let caseId =
                                                    DisclosureReads.target
                                                        request.Target
                                                        (view.Case |> Option.map _.CaseId)

                                                return
                                                    DisclosureReads.observe
                                                        services.Principal
                                                        view.Actor
                                                        DisclosureKind.CaseTimeline
                                                        caseId
                                                        None
                                                        outcome
                                                    |> DisclosureReads.withPage (fun page ->
                                                        DisclosureReads.rows page.Items)
                                        }
                            }

                    return! finalize services token result
        }

    let read services request token =
        readWith true DisclosureReads.complete RequestRefusals.protectedRead services request token

    let forExport services request token =
        readWith
            false
            DisclosureReads.completePreparation
            RequestRefusals.read
            services
            request
            token
