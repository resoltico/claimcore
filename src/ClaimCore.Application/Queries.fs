namespace ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Every read authorizes and decodes its continuation inside one store snapshot.
module internal Queries =
    open QueryResults

    let fault = QueryResults.fault
    let failure = QueryResults.failure

    let caseRecord (services: ServiceContext) actor work record =
        Advice.caseRecord services actor work record

    let caseResult (services: ServiceContext) (view: CaseFacts) work =
        let principal = services.Principal

        if not (CaseExecution.authorize principal view.Actor Capability.ReadCase view.Case) then
            unavailable
        else
            match view.Case with
            | Some record ->
                caseRecord services view.Actor work record
                |> Result.map (fun model ->
                    { model with
                        Advice = CaseAdvice.duringMaintenance view.Maintenance model.Advice
                        Work =
                            if view.Maintenance.Active then
                                { model.Work with
                                    CanAssign = false
                                    CanUpdateSummary = false
                                    CanCreateReminder = false
                                }
                            else
                                model.Work
                    })
                |> succeeded
            | None -> Outcome.Failed CoreFault.StoreIntegrityError

    let get (services: ServiceContext) selector token =
        let store = services.Store

        task {
            match CaseExecution.validateSelector selector with
            | Error refusal -> return Outcome.Rejected refusal
            | Ok _ ->
                let! result =
                    store.Read(
                        {
                            ReadOperation.Work =
                                (fun tx ->
                                    task {
                                        let! view = ExecutionFacts.case services tx None selector

                                        match view.Case with
                                        | Some record when
                                            CaseExecution.authorize
                                                services.Principal
                                                view.Actor
                                                Capability.ReadCase
                                                view.Case
                                            ->
                                            let! work =
                                                CaseWorkReads.view
                                                    tx
                                                    (CaseWorkReads.basis services tx.ObservedAt)
                                                    record

                                            return
                                                work
                                                |> Result.map (caseResult services view)
                                                |> Result.defaultWith Outcome.Failed
                                        | _ -> return unavailable
                                    })
                            CancellationToken = token
                        }
                    )

                return readResult result
        }

    let assignees (services: ServiceContext) (request: AssigneeRequest) token =
        task {
            if not (validLimit request.Limit) then
                return invalidLimit ()
            else
                match CaseExecution.validateSelector request.Target with
                | Error refusal -> return Outcome.Rejected refusal
                | Ok() ->
                    let! result =
                        services.Store.Read
                            {
                                Work =
                                    fun tx ->
                                        task {
                                            let! record = ExecutionFacts.findCase tx request.Target

                                            let! actor =
                                                tx.Authority
                                                    {
                                                        Principal = services.Principal
                                                        CaseId = record |> Option.map _.CaseId
                                                    }

                                            match record with
                                            | Some record when
                                                Authority.can
                                                    services.Principal
                                                    actor
                                                    Capability.ReadCase
                                                    (Scope.Case record.CaseId)
                                                && Authority.can
                                                    services.Principal
                                                    actor
                                                    Capability.ManageCaseWork
                                                    (Scope.Case record.CaseId)
                                                && (CaseState.fields record.Snapshot.State).IsSome
                                                ->
                                                match
                                                    cursor
                                                        (Cursors.decodeAssignees
                                                            services.InstallationId
                                                            record.CaseId)
                                                        request.AfterCursor
                                                with
                                                | Error refusal -> return Outcome.Rejected refusal
                                                | Ok after ->
                                                    let! page =
                                                        tx.AssigneePage
                                                            {
                                                                CaseId = record.CaseId
                                                                ReadRoles =
                                                                    Authority.rolesFor
                                                                        Capability.ReadCase
                                                                WorkRoles =
                                                                    Authority.rolesFor
                                                                        Capability.ManageCaseWork
                                                                AfterActorId = after
                                                                Limit = request.Limit
                                                            }

                                                    if page.Items.Length > request.Limit then
                                                        return
                                                            Outcome.Failed
                                                                CoreFault.StoreIntegrityError
                                                    else
                                                        return
                                                            Outcome.Succeeded
                                                                {
                                                                    AssigneePage.Items = page.Items
                                                                    NextCursor =
                                                                        page.Next
                                                                        |> Option.map (
                                                                            Cursors.encodeAssignees
                                                                                services
                                                                                    .InstallationId
                                                                                record.CaseId
                                                                        )
                                                                }
                                            | _ -> return unavailable
                                        }
                                CancellationToken = token
                            }

                    return readResult result
        }

    let contextResult
        (services: ServiceContext)
        observedAt
        (maintenance: RegisterMaintenance)
        (actor: AuthorityView)
        =
        let productVersion = services.ProductVersion
        let calendar = services.Calendar
        let principal = services.Principal

        let active (requester: Actor) : ActorContext =
            {
                Self = Actor.currentReference requester
                Maintenance = maintenance
                InstallationId = services.InstallationId
                ActorId = requester.ActorId
                Runtime =
                    {
                        ProductVersion = productVersion
                        EffectiveBusinessDate = calendar.DateAt observedAt
                        TimeZoneId = calendar.TimeZoneId
                    }
                HasScopedCases = actor.HasScopedCases
                View = Advice.views principal actor
                Offers =
                    if maintenance.Active then
                        []
                    else
                        Advice.installationOffers principal actor
                Capability =
                    Capability.all
                    |> List.filter (fun capability ->
                        Authority.can principal actor capability Scope.Installation)
            }

        let state =
            match actor.Actor with
            | None ->
                let _, issuer, subject = PrincipalKey.parts principal

                Ok(
                    RequesterState.NotRegistered
                        {
                            Issuer = issuer
                            Subject = subject
                            Claims = services.Claims
                        }
                )
            | Some requester when requester.Principal <> principal ->
                Error CoreFault.StoreIntegrityError
            | Some requester when not requester.Enabled ->
                Ok(RequesterState.Disabled(Actor.currentReference requester))
            | Some requester when List.isEmpty actor.Installation && not actor.HasScopedCases ->
                Ok(
                    RequesterState.NoRoles
                        {
                            Self = Actor.currentReference requester
                            InstallationId = services.InstallationId
                            BusinessDate = calendar.DateAt observedAt
                            TimeZone = calendar.TimeZoneId
                        }
                )
            | Some requester -> Ok(RequesterState.Active(active requester))

        state
        |> Result.map (fun state -> { ContextReply.Requester = state })
        |> Result.map Outcome.Succeeded
        |> Result.defaultWith Outcome.Failed

    let context (services: ServiceContext) token =
        task {
            let! result =
                services.Store.Context
                    {
                        Principal = services.Principal
                        Claims = services.Claims
                        CancellationToken = token
                    }

            return
                result
                |> Result.map (fun facts ->
                    contextResult
                        services
                        facts.ObservedAt
                        facts.Facts.Maintenance
                        facts.Facts.Authority)
                |> readResult
        }

    let selectCases installation principal basis (request: CaseListRequest) actor =
        currentActor principal actor
        |> Result.bind (fun actor ->
            match Authority.visibility principal actor with
            | None -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)
            | Some visibility ->
                CaseListFilter.admit request.Filter
                |> Result.mapError (fun violation ->
                    Refusal.invalidInput (InputTarget.Text, violation))
                |> Result.bind (fun filter ->
                    cursor (Cursors.decodeCases installation filter) request.Page.AfterCursor
                    |> Result.map (fun after ->
                        {
                            DateBasis = basis
                            Visibility = visibility
                            ActorId = actor.Actor |> Option.map _.ActorId |> Option.get
                            ReadRoles = Authority.readRoles principal
                            ForbiddenRoles = Authority.invalidRoles principal
                            Limit = request.Page.Limit
                            Filter = filter
                            AfterPosition = after
                        })))


    let private summary installation work next record =
        CaseExecution.current installation record
        |> Result.map (fun _ ->
            {
                CaseSummary.CaseId = record.Snapshot.CaseId
                Reference = record.Snapshot.Reference
                Revision = record.Snapshot.Revision
                ChangedAt = record.Index.ChangedAt
                Headline = CaseHeadline.ofState (CaseWorkReads.facts record work).State
                Attention =
                    CaseAttention.at work.Basis.BusinessDate (CaseWorkReads.facts record work)
                NextAction =
                    next
                    |> Option.map (fun reminder ->
                        {
                            CaseNextAction.ReminderId = Reminder.id reminder
                            Action = Reminder.action reminder
                            Due = Reminder.due reminder
                            DueState = work.Due
                        })
                Responsible = work.Responsible
                PendingReminders = work.PendingReminders
                State =
                    CaseState.map
                        (fun (fields: CaseFields) ->
                            {
                                CaseSummaryFacts.Fields =
                                    CaseFields.listedValues CaseFields.declarations fields
                            })
                        record.Snapshot.State
            })

    let private summaries
        (services: ServiceContext)
        (tx: IReadTx)
        basis
        (items: CasePageItem list)
        =
        task {
            let records = items |> List.map _.Case

            let supplied =
                items
                |> List.choose (fun item ->
                    item.WorkSummary |> Option.map (fun work -> item.Case.CaseId, work))
                |> Map.ofList

            let! work = CaseWorkReads.viewsFromSummaries tx basis records supplied

            return
                work
                |> Result.bind (fun work ->
                    records
                    |> List.fold
                        (fun accumulated record ->
                            accumulated
                            |> Result.bind (fun values ->
                                summary
                                    services.InstallationId
                                    (Map.find record.CaseId work)
                                    (supplied |> Map.tryFind record.CaseId |> Option.bind _.Next)
                                    record
                                |> Result.map (fun value -> value :: values)))
                        (Ok [])
                    |> Result.map List.rev)
        }

    let private exactReference (services: ServiceContext) (tx: IReadTx) text =
        task {
            match text |> Option.bind (CaseReference.parse >> Result.toOption) with
            | None -> return None
            | Some reference ->
                let! record = tx.FindCaseByReference(CaseReference.format reference)

                match record with
                | None -> return None
                | Some record ->
                    let! actor =
                        tx.Authority
                            {
                                Principal = services.Principal
                                CaseId = Some record.CaseId
                            }

                    return
                        if
                            CaseExecution.authorize
                                services.Principal
                                actor
                                Capability.ReadCase
                                (Some record)
                        then
                            Some
                                {
                                    CaseRef.CaseId = record.CaseId
                                    CaseReference = record.Snapshot.Reference
                                }
                        else
                            None
        }

    let list (services: ServiceContext) (request: CaseListRequest) token =
        let store = services.Store
        let principal = services.Principal

        task {
            if not (validLimit request.Page.Limit) then
                return invalidLimit ()
            else
                let! result =
                    store.Read(
                        {
                            ReadOperation.Work =
                                (fun tx ->
                                    task {
                                        let! facts = tx.GlobalReadFacts principal
                                        let actor = facts.Authority

                                        match
                                            selectCases
                                                services.InstallationId
                                                principal
                                                (CaseWorkReads.basis services tx.ObservedAt)
                                                request
                                                actor
                                        with
                                        | Error refusal -> return Outcome.Rejected refusal
                                        | Ok selection ->
                                            let! page = tx.CasePage selection
                                            let maintenance = facts.Maintenance

                                            if page.Items.Length > Paging.MaximumPageSize then
                                                return Outcome.Failed CoreFault.StoreIntegrityError
                                            else
                                                let! items =
                                                    summaries
                                                        services
                                                        tx
                                                        selection.DateBasis
                                                        page.Items

                                                let! exact =
                                                    exactReference services tx request.Filter.Text

                                                return
                                                    items
                                                    |> Result.map (fun items ->
                                                        {
                                                            CaseSummaryPage.Basis =
                                                                selection.DateBasis
                                                            Items = items
                                                            ExactReference = exact
                                                            Offers =
                                                                if maintenance.Active then
                                                                    []
                                                                else
                                                                    Advice.installationOffers
                                                                        principal
                                                                        actor
                                                            NextCursor =
                                                                page.Next
                                                                |> Option.map (
                                                                    Cursors.encodeCases
                                                                        services.InstallationId
                                                                        selection.Filter
                                                                )
                                                        })
                                                    |> succeeded
                                    })
                            CancellationToken = token
                        }
                    )

                return readResult result
        }

    let caseSummary (services: ServiceContext) (filter: CaseListFilter<string>) token =
        task {
            let! result =
                services.Store.Read
                    {
                        Work =
                            fun tx ->
                                task {
                                    let! actor =
                                        tx.Authority
                                            {
                                                Principal = services.Principal
                                                CaseId = None
                                            }

                                    let request =
                                        {
                                            CaseListRequest.Page = { Limit = 1; AfterCursor = None }
                                            Filter = filter
                                        }

                                    match
                                        selectCases
                                            services.InstallationId
                                            services.Principal
                                            (CaseWorkReads.basis services tx.ObservedAt)
                                            request
                                            actor
                                    with
                                    | Error refusal -> return Outcome.Rejected refusal
                                    | Ok selection ->
                                        let! summary = tx.CaseSummary selection.Selection
                                        return Outcome.Succeeded summary
                                }
                        CancellationToken = token
                    }

            return readResult result
        }

    let selectHistory installation principal (request: HistoryRequest) actor record =
        let capability =
            if request.Detail = HistoryDetail.Full then
                Capability.ReadHistoryFull
            else
                Capability.ReadHistory

        if not (CaseExecution.authorize principal actor capability record) then
            Error(Refusal.Access AccessRefusal.ResourceUnavailable)
        else
            currentActor principal actor
            |> Result.bind (fun _ ->
                match record with
                | None -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)
                | Some record ->
                    cursor (Cursors.decodeHistory installation record.CaseId) request.AfterCursor
                    |> Result.map (Option.defaultValue Revision.zero))

    let private historyEntries
        (services: ServiceContext)
        actor
        current
        detail
        authors
        (slice: CaseHistorySlice)
        =
        if detail = HistoryDetail.Summary then
            slice.Page.Items
            |> List.map (LedgerEntries.summary current >> Result.map HistoryEntry.SummaryEntry)
            |> collect
        else
            let all = slice.Page.Items @ Option.toList slice.Previous

            all
            |> List.map (fun entry ->
                LedgerEntries.validate services.InstallationId entry
                |> Result.bind (
                    LedgerEntries.receipt
                        services.Principal
                        actor
                        (Map.tryFind entry.ActorId authors)
                        (ReceiptCurrent.Case(Some current))
                ))
            |> collect
            |> Result.bind (fun receipts ->
                match receipts with
                | [] -> Ok []
                | _ :: tail ->
                    let previous = (tail |> List.map Some) @ [ None ]

                    List.zip receipts previous
                    |> List.truncate slice.Page.Items.Length
                    |> List.map (fun (receipt, previous) ->
                        CaseChanges.history previous receipt
                        |> Result.map (fun difference ->
                            HistoryEntry.FullEntry
                                {
                                    Receipt = receipt
                                    Difference = difference
                                }))
                    |> collect)

    let history (services: ServiceContext) (request: HistoryRequest) token =
        let store = services.Store
        let installation = services.InstallationId
        let principal = services.Principal

        task {
            match CaseExecution.validateSelector request.Target with
            | Error refusal -> return Outcome.Rejected refusal
            | Ok _ when not (validLimit request.Limit) -> return invalidLimit ()
            | Ok _ ->
                let! result =
                    store.Read(
                        {
                            ReadOperation.Work =
                                (fun tx ->
                                    task {
                                        let! record = ExecutionFacts.findCase tx request.Target

                                        let! actor =
                                            ExecutionFacts.authority
                                                tx
                                                principal
                                                (record |> Option.map _.CaseId |> Option.toList)

                                        match
                                            selectHistory
                                                installation
                                                principal
                                                request
                                                actor
                                                record
                                        with
                                        | Error refusal -> return Outcome.Rejected refusal
                                        | Ok after ->
                                            match record with
                                            | None ->
                                                return Outcome.Failed CoreFault.StoreIntegrityError
                                            | Some current ->
                                                let! slice =
                                                    tx.HistoryPage(
                                                        {
                                                            HistoryPageQuery.CaseId =
                                                                current.CaseId
                                                            AfterRevision = after
                                                            Limit = request.Limit
                                                        }
                                                    )

                                                let! authors =
                                                    if request.Detail = HistoryDetail.Summary then
                                                        System.Threading.Tasks.Task.FromResult
                                                            Map.empty
                                                    else
                                                        tx.ActorRefs(
                                                            (slice.Page.Items
                                                             @ Option.toList slice.Previous)
                                                            |> List.map _.ActorId
                                                            |> List.distinct
                                                        )

                                                return
                                                    CaseExecution.current installation current
                                                    |> Result.bind (fun _ ->
                                                        historyEntries
                                                            services
                                                            actor
                                                            current
                                                            request.Detail
                                                            authors
                                                            slice)
                                                    |> Result.map (fun entries ->
                                                        {
                                                            HistoryResultPage.CaseId =
                                                                current.CaseId
                                                            Reference = current.Snapshot.Reference
                                                            Entries = entries
                                                            NextCursor =
                                                                slice.Page.Next
                                                                |> Option.map (
                                                                    Cursors.encodeHistory
                                                                        installation
                                                                        current.CaseId
                                                                )
                                                        })
                                                    |> succeeded
                                    })
                            CancellationToken = token
                        }
                    )

                return readResult result
        }

    let observe (services: ServiceContext) operation token =
        let store = services.Store
        let installation = services.InstallationId
        let principal = services.Principal

        let operationId = OperationId.create operation

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
                let operation = operationId.Value

                let! result =
                    store.Read(
                        {
                            ReadOperation.Work =
                                (fun tx ->
                                    task {
                                        let! actor, entry, current =
                                            ExecutionFacts.operation tx principal operation

                                        match entry with
                                        | Some entry when
                                            LedgerEntries.canObserve principal actor entry.Subject
                                            ->
                                            if entry.OperationId <> operation then
                                                return Outcome.Failed CoreFault.StoreIntegrityError
                                            else
                                                let! authors = tx.ActorRefs [ entry.ActorId ]

                                                return
                                                    LedgerEntries.validateOperation
                                                        installation
                                                        entry
                                                    |> Result.bind (
                                                        LedgerEntries.receiptOperation
                                                            principal
                                                            actor
                                                            (Map.tryFind entry.ActorId authors)
                                                            current
                                                    )
                                                    |> succeeded
                                        | _ -> return unavailable
                                    })
                            CancellationToken = token
                        }
                    )

                return readResult result
        }

    let selectActors = AuthorityQueries.selectActors
    let actors = AuthorityQueries.actors
    let selectGrants = AuthorityQueries.selectGrants
    let grants = AuthorityQueries.grants
    let authorityHistory = AuthorityQueries.history
