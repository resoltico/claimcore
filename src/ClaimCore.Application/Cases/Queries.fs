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

    let caseRecord
        (services: ServiceContext)
        actor
        work
        stages
        changedAt
        recordedBy
        payments
        record
        =
        Advice.caseRecord services actor work stages changedAt recordedBy payments record

    let caseResult
        (services: ServiceContext)
        (view: CaseFacts)
        work
        stages
        changedAt
        recordedBy
        payments
        =
        let principal = services.Principal

        if not (CaseExecution.authorize principal view.Actor Capability.ReadCase view.Case) then
            unavailable
        else
            match view.Case with
            | Some record ->
                caseRecord services view.Actor work stages changedAt recordedBy payments record
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

    let private disposition installation (record: StoredCase) (facts: CaseStageFacts) =
        let invalid = Error CoreFault.StoreIntegrityError

        match facts.LastAct, facts.DispositionEntry with
        | (CaseActKind.Void | CaseActKind.Reinstate), Some entry ->
            match entry.Subject with
            | EntryTarget.Case(id, revision, act, _) when
                id = record.CaseId
                && revision = record.Snapshot.Revision
                && act = facts.LastAct
                && entry.ActorId = facts.RecordedBy
                && entry.RecordedAt = facts.RecordedAt
                ->
                match
                    LedgerEntries.validate installation entry |> Result.map LedgerEntries.content
                with
                | Ok(EntryContent.Case(request, Some decision)) when
                    decision.After = record.Snapshot
                    ->
                    match request.Act with
                    | CaseAct.Void reason -> Ok(Some(CaseDisposition.Voided reason))
                    | CaseAct.Reinstate reason -> Ok(Some(CaseDisposition.Reinstated reason))
                    | _ -> invalid
                | _ -> invalid
            | _ -> invalid
        | (CaseActKind.Void | CaseActKind.Reinstate), None -> invalid
        | _, None -> Ok None
        | _, Some _ -> invalid

    let private stageDisplay installation (tx: IReadTx) (record: StoredCase) =
        task {
            let! facts = tx.CaseStageFacts record.CaseId

            match facts with
            | Some facts when facts.Revision = record.Snapshot.Revision ->
                let! authors = tx.ActorRefs [ facts.RecordedBy ]

                return
                    match
                        CaseStages.describe
                            record.Snapshot.State
                            (Some facts.OpenedOn)
                            facts.ClosedOn,
                        Map.tryFind facts.RecordedBy authors,
                        disposition installation record facts
                    with
                    | Ok stages, Some author, Ok disposition when author.ActorId = facts.RecordedBy ->
                        Ok(stages, disposition, facts.RecordedAt, author)
                    | _ -> Error CoreFault.StoreIntegrityError
            | _ -> return Error CoreFault.StoreIntegrityError
        }

    let get (services: ServiceContext) selector token =
        let store = services.Store

        task {
            match CaseExecution.validateSelector selector with
            | Error refusal -> return! RequestRefusals.read services token refusal
            | Ok _ ->
                let! result =
                    store.Read(
                        {
                            ReadOperation.Work =
                                (fun tx ->
                                    task {
                                        let! view =
                                            ExecutionFacts.case services tx None selector None

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
                                                    (ServiceContext.basis services tx.ObservedAt)
                                                    record

                                            let! stages =
                                                stageDisplay services.InstallationId tx record

                                            match work, stages with
                                            | Ok work,
                                              Ok(stages, disposition, changedAt, recordedBy) ->
                                                let! nextActions =
                                                    CaseWorkReads.nextActions
                                                        services
                                                        tx
                                                        work
                                                        view.Actor
                                                        view.Maintenance.Active
                                                        record

                                                let! notes =
                                                    NoteQueries.current
                                                        services
                                                        tx
                                                        view
                                                        record
                                                        None
                                                        3

                                                let! payments =
                                                    match
                                                        CaseState.fields record.Snapshot.State
                                                    with
                                                    | None ->
                                                        System.Threading.Tasks.Task.FromResult []
                                                    | Some _ ->
                                                        tx.LatestPayments(
                                                            record.CaseId,
                                                            PaymentPaging.PreviewLimit
                                                        )

                                                let count =
                                                    CaseState.fields record.Snapshot.State
                                                    |> Option.map (fun fields ->
                                                        fields.Finance.PaymentCount)
                                                    |> Option.defaultValue 0L

                                                let validPayments =
                                                    payments.Length =
                                                        int (
                                                            min
                                                                (int64 PaymentPaging.PreviewLimit)
                                                                count
                                                        )

                                                return
                                                    match nextActions, notes with
                                                    | Error fault, _
                                                    | _, Error fault -> Outcome.Failed fault
                                                    | Ok _, Ok _ when not validPayments ->
                                                        Outcome.Failed
                                                            CoreFault.StoreIntegrityError
                                                    | Ok items, Ok notes ->
                                                        caseResult
                                                            services
                                                            view
                                                            work
                                                            stages
                                                            changedAt
                                                            recordedBy
                                                            payments
                                                        |> Outcome.fold
                                                            (fun current ->
                                                                Outcome.Succeeded
                                                                    { current with
                                                                        Disposition = disposition
                                                                        NextActions = items
                                                                        Notes = notes.Notes
                                                                    })
                                                            Outcome.Rejected
                                                            Outcome.Failed
                                            | Error fault, _
                                            | _, Error fault -> return Outcome.Failed fault
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
                return! RequestRefusals.invalidLimit services token
            else
                match CaseExecution.validateSelector request.Target with
                | Error refusal -> return! RequestRefusals.read services token refusal
                | Ok() ->
                    let! result =
                        services.Store.Read
                            {
                                Work =
                                    fun tx ->
                                        task {
                                            let! record = ExecutionFacts.findCase tx request.Target

                                            let! actor =
                                                tx.Access
                                                    {
                                                        Principal = services.Principal
                                                        CaseId = record |> Option.map _.CaseId
                                                    }

                                            match record with
                                            | Some record when
                                                Access.can
                                                    services.Principal
                                                    actor
                                                    Capability.ReadCase
                                                    (Scope.Case record.CaseId)
                                                && Access.can
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
                                                                    Access.rolesFor
                                                                        Capability.ReadCase
                                                                WorkRoles =
                                                                    Access.rolesFor
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
                                                                    AssigneePage.Basis =
                                                                        ServiceContext.basis
                                                                            services
                                                                            tx.ObservedAt
                                                                    Items = page.Items
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
        (actor: AccessView)
        (roles: ActorRoleSummary list)
        =
        let principal = services.Principal

        let active (requester: Actor) : ActorContext =
            let _, issuer, _ = PrincipalKey.parts principal

            {
                Issuer = issuer
                Roles = roles |> ActorRoleSummary.ordered
                Self = Actor.currentReference requester
                Maintenance = maintenance
                InstallationId = services.InstallationId
                ActorId = requester.ActorId
                Runtime = ServiceContext.runtime services observedAt
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
                        Access.can principal actor capability Scope.Installation)
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
            | Some requester when
                AccountState.classify requester.Enabled actor.HasAssignedRoles =
                    AccountState.Disabled
                ->
                Ok(RequesterState.Disabled(Actor.currentReference requester))
            | Some requester when
                AccountState.classify requester.Enabled actor.HasAssignedRoles =
                    AccountState.NoRoles
                ->
                let basis = ServiceContext.basis services observedAt

                Ok(
                    RequesterState.NoRoles
                        {
                            Self = Actor.currentReference requester
                            InstallationId = services.InstallationId
                            BusinessDate = basis.BusinessDate
                            TimeZone = basis.TimeZoneId
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
                        facts.Facts.People
                        facts.Facts.AssignedRoles)
                |> readResult
        }

    let selectCases installation principal basis (request: CaseListRequest) actor =
        currentActor principal actor
        |> Result.bind (fun actor ->
            match Access.visibility principal actor with
            | None -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)
            | Some visibility ->
                CaseListFilter.admit request.Filter
                |> Result.mapError (fun violation ->
                    Refusal.invalidInput (InputTarget.Text, violation))
                |> Result.bind (fun filter ->
                    cursor (Cursors.decodeCases installation filter) request.Page.AfterCursor
                    |> Result.map (fun after ->
                        {
                            CasePageQuery.Selection =
                                {
                                    DateBasis = basis
                                    Visibility = visibility
                                    ActorId = actor.Actor |> Option.map _.ActorId |> Option.get
                                    ReadRoles = Access.readRoles principal
                                    ForbiddenRoles = Access.invalidRoles principal
                                    Filter = filter
                                }
                            Limit = request.Page.Limit
                            AfterPosition = after
                        })))


    let private summary installation principal actor paused (work: CaseWorkView) next record =
        CaseExecution.current installation record
        |> Result.map (fun _ ->
            {
                CaseSummary.CaseId = record.Snapshot.CaseId
                Reference = record.Snapshot.Reference
                Revision = record.Snapshot.Revision
                WorkRevision = work.Revision
                CanAssign = CaseWorkReads.canAssign principal actor paused record.Snapshot
                ChangedAt = record.Index.ChangedAt
                Headline = CaseHeadline.ofState (CaseWorkReads.facts record work).State
                Stages = CaseStages.outline record.Snapshot.State
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
                Paid =
                    CaseState.fields record.Snapshot.State
                    |> Option.bind (fun fields ->
                        FinancialPosition.totals fields.Finance |> Option.map _.Paid)
                PaymentCount =
                    CaseState.fields record.Snapshot.State
                    |> Option.map (fun fields -> fields.Finance.PaymentCount)
                    |> Option.defaultValue 0L
                LastPaymentDate =
                    CaseState.fields record.Snapshot.State
                    |> Option.bind (fun fields ->
                        fields.Finance.PaymentDates |> Option.map _.Latest)
                State =
                    CaseState.map
                        (fun (fields: CaseFields) ->
                            {
                                CaseSummaryFacts.Fields =
                                    CaseSummaryFields.values CaseSummaryFields.declarations fields
                            })
                        record.Snapshot.State
            })

    let private summaries
        (services: ServiceContext)
        (tx: IReadTx)
        basis
        paused
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
            let! actor = tx.AccessCases(services.Principal, records |> List.map _.CaseId)

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
                                    services.Principal
                                    actor
                                    paused
                                    (Map.find record.CaseId work)
                                    (supplied |> Map.tryFind record.CaseId |> Option.bind _.Next)
                                    record
                                |> Result.map (fun value -> value :: values)))
                        (Ok [])
                    |> Result.map List.rev)
        }

    let private exactReference
        (services: ServiceContext)
        (tx: IReadTx)
        (items: CaseSummary list)
        text
        =
        task {
            match text |> Option.bind (CaseReference.parse >> Result.toOption) with
            | None -> return None
            | Some reference ->
                let key = CaseReference.format reference |> CaseReference.key

                match
                    items
                    |> List.tryFind (fun item ->
                        CaseReference.key (CaseReference.format item.Reference) = key)
                with
                | Some item ->
                    // This page item has already passed validation and snapshot disclosure.
                    return
                        Some
                            {
                                CaseRef.CaseId = item.CaseId
                                CaseReference = item.Reference
                            }
                | None ->
                    let! record = tx.FindCaseByReference(CaseReference.format reference)

                    match record with
                    | None -> return None
                    | Some record ->
                        let! actor =
                            tx.Access
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
                return! RequestRefusals.invalidLimit services token
            else
                let! result =
                    store.Read(
                        {
                            ReadOperation.Work =
                                (fun tx ->
                                    task {
                                        let! facts = tx.GlobalReadFacts principal
                                        let actor = facts.People

                                        match
                                            selectCases
                                                services.InstallationId
                                                principal
                                                (ServiceContext.basis services tx.ObservedAt)
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
                                                        selection.Selection.DateBasis
                                                        facts.Maintenance.Active
                                                        page.Items

                                                match items with
                                                | Error fault -> return Outcome.Failed fault
                                                | Ok items ->
                                                    let! exact =
                                                        exactReference
                                                            services
                                                            tx
                                                            items
                                                            request.Filter.Text

                                                    return
                                                        Outcome.Succeeded
                                                            {
                                                                CaseSummaryPage.Basis =
                                                                    selection.Selection.DateBasis
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
                                                                            selection.Selection
                                                                                .Filter
                                                                    )
                                                            }

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
                                        tx.Access
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
                                            (ServiceContext.basis services tx.ObservedAt)
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

    let selectActors = PeopleQueries.selectActors
    let actors = PeopleQueries.actors
    let selectGrants = PeopleQueries.selectGrants
    let grants = PeopleQueries.grants
    let peopleHistory = PeopleQueries.history
