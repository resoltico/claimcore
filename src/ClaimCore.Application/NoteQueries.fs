namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

module internal NoteQueries =
    let current
        (services: ServiceContext)
        (tx: IReadTx)
        (view: CaseFacts)
        (parent: StoredCase)
        before
        limit
        =
        task {
            let! rows =
                tx.NotePage
                    {
                        CaseId = parent.CaseId
                        Before = before
                        Limit = limit
                    }

            let! authors = tx.ActorRefs(rows.Items |> List.map _.RecordedBy |> List.distinct)

            let writable =
                CaseWorkPolicy.canEdit
                    services.Principal
                    view.Actor
                    view.Maintenance.Active
                    parent.Snapshot

            let items =
                rows.Items
                |> List.map (fun stored ->
                    match Note.text stored.Note, Map.tryFind stored.RecordedBy authors with
                    | Some text, Some author when
                        Note.caseId stored.Note = parent.CaseId
                        && author.ActorId = stored.RecordedBy
                        ->
                        Ok
                            {
                                NoteReadModel.NoteId = Note.id stored.Note
                                Text = text
                                RecordedAt = stored.RecordedAt
                                RecordedBy = author
                                CanRemove = writable
                            }
                    | _ -> Error CoreFault.StoreIntegrityError)
                |> QueryResults.collect

            return
                items
                |> Result.map (fun items ->
                    {
                        CaseNotesPage.Basis = ServiceContext.basis services tx.ObservedAt
                        CaseId = parent.CaseId
                        Reference = parent.Snapshot.Reference
                        Notes =
                            {
                                Items = items
                                Count = rows.Count
                                CanAdd = writable
                            }
                        NextCursor =
                            rows.Next
                            |> Option.map (
                                Cursors.encodeNotes services.InstallationId parent.CaseId
                            )
                    })
        }

    let page (services: ServiceContext) (request: CaseNotesRequest) token =
        task {
            if request.Limit < 1 || request.Limit > NotePaging.MaximumLimit then
                return!
                    RequestRefusals.read
                        services
                        token
                        (Refusal.invalidInput (
                            InputTarget.Limit,
                            Violation.PageLimitRange NotePaging.MaximumLimit
                        ))
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
                                                    Capability.ReadCase
                                                    (Scope.Case parent.CaseId)
                                                ->
                                                match
                                                    QueryResults.cursor
                                                        (Cursors.decodeNotes
                                                            services.InstallationId
                                                            parent.CaseId)
                                                        request.AfterCursor
                                                with
                                                | Error refusal -> return Outcome.Rejected refusal
                                                | Ok before ->
                                                    let! page =
                                                        current
                                                            services
                                                            tx
                                                            view
                                                            parent
                                                            before
                                                            request.Limit

                                                    return Outcome.ofResult page
                                            | _ -> return QueryResults.unavailable
                                        }
                            }

                    return QueryResults.readResult result
        }
