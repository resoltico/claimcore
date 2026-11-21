namespace ClaimCore.Application.Reads

open ClaimCore.Application.Storage

open ClaimCore.Application.Execution

open ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Every read authorizes and decodes its continuation inside one store snapshot.
module internal Queries =
    open QueryResults

    let fault = QueryResults.fault
    let failure = QueryResults.failure

    let caseResult
        (services: ServiceContext)
        (tx: ReadTx)
        (view: CaseReadFacts)
        work
        stages
        changedAt
        recordedBy
        payments
        =
        task {
            if
                not (
                    CaseRecords.authorize
                        services.Principal
                        view.Actor
                        Capability.ReadCase
                        view.Case
                )
            then
                return unavailable
            else
                match view.Case with
                | None -> return Outcome.Failed CoreFault.StoreIntegrityError
                | Some record ->
                    let! model =
                        Advice.caseRecord
                            services
                            tx
                            view.Actor
                            view.Maintenance
                            view.FamilySequence
                            work
                            stages
                            changedAt
                            recordedBy
                            payments
                            record

                    return model |> succeeded
        }

    let private stageDisplay _installation (tx: ReadTx) (record: StoredCase) =
        task {
            let! facts = FamilyReads.caseStageFacts tx record.CaseId

            match facts with
            | Some facts when
                facts.Revision = record.Snapshot.Revision && facts.Snapshot = record.Snapshot
                ->
                let! authors = PeopleReads.actorRefs tx [ facts.RecordedBy ]

                let! paidDates =
                    PaymentStorage.latestPaidDates
                        tx.Connection
                        [ record.CaseId ]
                        tx.CancellationToken

                return
                    match
                        CaseStages.describe
                            record.Snapshot.State
                            (Some facts.OpenedOn)
                            facts.ClosedOn
                            (Map.tryFind record.CaseId paidDates),
                        Map.tryFind facts.RecordedBy authors,
                        Ok facts.Disposition
                    with
                    | Ok stages, Some author, Ok disposition when author.ActorId = facts.RecordedBy ->
                        Ok(stages, disposition, facts.RecordedAt, author)
                    | _ -> Error CoreFault.StoreIntegrityError
            | _ -> return Error CoreFault.StoreIntegrityError
        }

    let private getInSnapshot
        (services: ServiceContext)
        selector
        (tx: ReadTx)
        (view: CaseReadFacts)
        =
        task {
            match view.Case with
            | Some record when
                CaseRecords.authorize services.Principal view.Actor Capability.ReadCase view.Case
                ->
                let! work =
                    CaseWorkReads.view tx (ServiceContext.basis services tx.ObservedAt) record

                let! stages = stageDisplay services.InstallationId tx record

                match work, stages with
                | Ok work, Ok(stages, disposition, changedAt, recordedBy) ->
                    let! nextActions =
                        CaseWorkReads.nextActions
                            services
                            tx
                            work
                            view.Actor
                            view.Maintenance.Active
                            record

                    let! notes = NoteQueries.current services tx view record None 3

                    let! payments =
                        match CaseState.fields record.Snapshot.State with
                        | None -> System.Threading.Tasks.Task.FromResult []
                        | Some _ ->
                            PaymentReads.latestPayments
                                tx
                                (record.CaseId, PaymentPaging.PreviewLimit)

                    let count =
                        CaseState.fields record.Snapshot.State
                        |> Option.map (fun fields -> fields.Finance.PaymentCount)
                        |> Option.defaultValue 0L

                    let validPayments =
                        payments.Length = int (min (int64 PaymentPaging.PreviewLimit) count)

                    match nextActions, notes with
                    | Error fault, _
                    | _, Error fault -> return Outcome.Failed fault
                    | Ok _, Ok _ when not validPayments ->
                        return Outcome.Failed CoreFault.StoreIntegrityError
                    | Ok items, Ok notes ->
                        let! relatedCases = CaseLinkReads.read services tx view record

                        let! matchedReference =
                            CaseIdentityReads.matchedEarlier
                                tx
                                record.CaseId
                                record.Snapshot.Reference
                                selector

                        let! currentResult =
                            caseResult services tx view work stages changedAt recordedBy payments

                        return
                            currentResult
                            |> Outcome.fold
                                (fun current ->
                                    Outcome.Succeeded
                                        { current with
                                            MatchedReference = matchedReference
                                            RelatedCases = relatedCases
                                            Disposition = disposition
                                            NextActions = items
                                            Notes = notes.Notes
                                        })
                                Outcome.Rejected
                                Outcome.Failed
                | Error fault, _
                | _, Error fault -> return Outcome.Failed fault
            | _ -> return unavailable
        }

    let get (services: ServiceContext) selector token =
        task {
            match CaseRecords.validateSelector selector with
            | Error refusal -> return! RequestRefusals.read services token refusal
            | Ok _ ->
                let! result =
                    services.Store.Read
                        {
                            Work =
                                fun tx ->
                                    task {
                                        let! view = CaseReadContext.load services tx selector
                                        let! outcome = getInSnapshot services selector tx view

                                        let caseId =
                                            DisclosureReads.target
                                                selector
                                                (view.Case |> Option.map _.CaseId)

                                        return
                                            DisclosureReads.observe
                                                services.Principal
                                                view.Actor
                                                DisclosureKind.CaseRead
                                                caseId
                                                None
                                                outcome
                                    }
                            CancellationToken = token
                        }

                return! DisclosureReads.complete services token result
        }

    let assignees (services: ServiceContext) (request: AssigneeRequest) token =
        task {
            if not (validLimit request.Limit) then
                return! RequestRefusals.invalidLimit services token
            else
                match CaseRecords.validateSelector request.Target with
                | Error refusal -> return! RequestRefusals.read services token refusal
                | Ok() ->
                    let! result =
                        services.Store.Read
                            {
                                Work =
                                    fun tx ->
                                        task {
                                            let! record = CaseReadContext.find tx request.Target

                                            let! actor =
                                                AccessReads.access
                                                    tx
                                                    (services.Principal)
                                                    (record |> Option.map _.CaseId)

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
                                                    let! work = WorkReads.findWork tx record.CaseId

                                                    match work with
                                                    | Some work when
                                                        work.CaseId = record.CaseId
                                                        && (work.Snapshot
                                                            |> Option.exists (fun snapshot ->
                                                                snapshot.Revision = work.Revision))
                                                        ->
                                                        let! page =
                                                            AccessReads.assigneePage
                                                                tx
                                                                {
                                                                    CaseId = record.CaseId
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
                                                                        AssigneePage.CaseId =
                                                                            record.CaseId
                                                                        WorkRevision = work.Revision
                                                                        Basis =
                                                                            ServiceContext.basis
                                                                                services
                                                                                tx.ObservedAt
                                                                        Items = page.Items
                                                                        NextCursor =
                                                                            page.Next
                                                                            |> Option.map (
                                                                                Cursors
                                                                                    .encodeAssignees
                                                                                    services
                                                                                        .InstallationId
                                                                                    record.CaseId
                                                                            )
                                                                    }
                                                    | _ ->
                                                        return
                                                            Outcome.Failed
                                                                CoreFault.StoreIntegrityError
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

    let selectCases
        installation
        principal
        basis
        (request: CaseListRequest)
        actor
        : Result<TransactionPages.CaseQuery, Refusal> =
        currentActor principal actor
        |> Result.bind (fun actor ->
            match Access.visibility principal actor with
            | None -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)
            | Some visibility ->
                CaseListFilter.admit request.Filter
                |> Result.mapError (function
                    | CaseSelectionIssue.Text violation ->
                        Refusal.invalidInput (InputTarget.Text, violation)
                    | CaseSelectionIssue.ReversedRange upper -> Refusal.CaseDateRange upper)
                |> Result.bind (fun filter ->
                    cursor (Cursors.decodeCases installation filter) request.Page.AfterCursor
                    |> Result.map (fun after ->
                        {
                            TransactionPages.CaseQuery.Selection =
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


    let private summary
        installation
        principal
        actor
        paused
        (work: CaseWorkView)
        next
        lastPayment
        parties
        record
        =
        CaseRecords.current installation parties record
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
                NetPaid =
                    CaseState.fields record.Snapshot.State
                    |> Option.bind (fun fields -> FinancialPosition.totals fields.Finance)
                PaymentCount =
                    CaseState.fields record.Snapshot.State
                    |> Option.map (fun fields -> fields.Finance.PaymentCount)
                    |> Option.defaultValue 0L
                LastPaymentDate = lastPayment
                State =
                    CaseState.map
                        (fun (fields: CaseFields) ->
                            {
                                CaseSummaryFacts.Fields =
                                    CaseSummaryFields.values CaseSummaryFields.declarations fields
                                Parties =
                                    Vocabulary.all PartyRole.vocabulary
                                    |> List.map (fun role ->
                                        role, PartyContent.names role parties)
                                    |> Map.ofList
                            })
                        record.Snapshot.State
            })

    let private summaries
        (services: ServiceContext)
        (tx: ReadTx)
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

            let! parties =
                PartyStorage.current
                    tx.Connection
                    (records |> List.map _.CaseId)
                    tx.CancellationToken

            let! actor =
                AccessReads.accessCases tx (services.Principal, records |> List.map _.CaseId)

            let! paidDates =
                PaymentStorage.latestPaidDates
                    tx.Connection
                    (records |> List.map _.CaseId)
                    tx.CancellationToken

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
                                    (Map.tryFind record.CaseId paidDates)
                                    (Map.tryFind record.CaseId parties |> Option.defaultValue [])
                                    record
                                |> Result.map (fun value -> value :: values)))
                        (Ok [])
                    |> Result.map List.rev)
        }

    let private exactReference (services: ServiceContext) (tx: ReadTx) text =
        task {
            match text |> Option.bind (CaseReference.parse >> Result.toOption) with
            | None -> return None
            | Some reference ->
                let text = CaseReference.format reference
                let! record = FamilyReads.findCaseByReference tx text

                match record with
                | None -> return None
                | Some record ->
                    let! actor = AccessReads.access tx services.Principal (Some record.CaseId)

                    if
                        CaseRecords.authorize
                            services.Principal
                            actor
                            Capability.ReadCase
                            (Some record)
                    then
                        let! matched =
                            CaseIdentityReads.matchedEarlier
                                tx
                                record.CaseId
                                record.Snapshot.Reference
                                (CaseSelector.Reference text)

                        return
                            Some
                                {
                                    ExactCaseReference.CaseId = record.CaseId
                                    CaseReference = record.Snapshot.Reference
                                    MatchedReference = matched
                                }
                    else
                        return None
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
                                        let! facts = AccessReads.globalReadFacts tx principal
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
                                            let! page = FamilyReads.casePage tx selection
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

    let caseSummary (services: ServiceContext) (summary: CaseSummaryRequest) token =
        task {
            if not (validLimit summary.Page.Limit) then
                return! RequestRefusals.invalidLimit services token
            else
                let! result =
                    services.Store.Read
                        {
                            Work =
                                fun tx ->
                                    task {
                                        let! actor = AccessReads.access tx services.Principal None
                                        let basis = ServiceContext.basis services tx.ObservedAt

                                        let request =
                                            {
                                                CaseListRequest.Page =
                                                    {
                                                        Limit = summary.Page.Limit
                                                        AfterCursor = None
                                                    }
                                                Filter = summary.Selection.Filter
                                            }

                                        match
                                            selectCases
                                                services.InstallationId
                                                services.Principal
                                                basis
                                                request
                                                actor
                                        with
                                        | Error refusal -> return Outcome.Rejected refusal
                                        | Ok selected ->
                                            let selection = selected.Selection
                                            let grouping = summary.Selection.GroupBy
                                            let date = CalendarDate.ofDate basis.BusinessDate

                                            let scope =
                                                Cursors.decodeCaseReport
                                                    services.InstallationId
                                                    selection.ActorId
                                                    date
                                                    selection.Filter
                                                    grouping

                                            match cursor scope summary.Page.AfterCursor with
                                            | Error refusal -> return Outcome.Rejected refusal
                                            | Ok position ->
                                                let! counts, rows, next =
                                                    FamilyReads.caseSummary
                                                        tx
                                                        selection
                                                        grouping
                                                        position
                                                        summary.Page.Limit

                                                return
                                                    Outcome.Succeeded
                                                        {
                                                            Basis = basis
                                                            Counts = counts
                                                            GroupBy = grouping
                                                            Rows = rows
                                                            NextCursor =
                                                                next
                                                                |> Option.map (
                                                                    Cursors.encodeCaseReport
                                                                        services.InstallationId
                                                                        selection.ActorId
                                                                        date
                                                                        selection.Filter
                                                                        grouping
                                                                )
                                                        }
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
