module ClaimCore.TestSupport.CaseWorkContract

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected read success: %A" other

let private execute core operation reference revision act =
    Service.execute core (Inputs.caseOperation (request operation reference revision act)) token

let private get core caseId =
    Service.get core { Target = CaseSelector.Id caseId } token

let private candidates core caseId after limit =
    Service.assignees
        core
        {
            Target = CaseSelector.Id caseId
            AfterCursor = after
            Limit = limit
        }
        token

let private list core filter =
    Service.list
        core
        {
            Page = { AfterCursor = None; Limit = 50 }
            Filter = filter
        }
        token

let internal assignmentAndDisclosure fixture =
    task {
        let! owner = caseWorker fixture

        let person =
            PrincipalKey.human "https://issuer.example/" "case-work-assignee"
            |> Result.defaultWith (string >> failtest)

        let reader =
            PrincipalKey.human "https://issuer.example/" "scoped-reader"
            |> Result.defaultWith (string >> failtest)

        let automation =
            PrincipalKey.service "https://issuer.example/" "service-handler"
            |> Result.defaultWith (string >> failtest)

        for operation, principal in [ 10, person; 11, reader; 12, automation ] do
            let! registered =
                Service.execute
                    owner
                    (OperationInput.Authority(
                        authorityRequest
                            (id operation)
                            principal
                            (AuthorityAct.Register ClaimCore.TestSupport.Inputs.actorProfile)
                    ))
                    token

            acceptedAuthority registered

        let! opened = execute owner 20 "WORK-A" 0L (CaseAct.Open registration)
        let caseId = accepted opened |> caseReceipt |> _.CaseId
        let! other = execute owner 21 "WORK-B" 0L (CaseAct.Open registration)
        let otherId = accepted other |> caseReceipt |> _.CaseId

        for operation, principal, role, scope in
            [
                22, person, Role.CaseHandler, GrantTarget.CaseId caseId
                23, reader, Role.CaseReader, GrantTarget.CaseId caseId
                24, automation, Role.CaseHandler, GrantTarget.Installation
            ] do
            let! changed =
                grant owner (id operation) principal { Role = role; Scope = scope } true

            acceptedAuthority changed

        let observations = ResizeArray<DateTimeOffset>()

        let observedStore =
            { new IStore with
                member _.Context query = fixture.Store.Context query
                member _.Write operation = fixture.Store.Write operation

                member _.Read operation =
                    fixture.Store.Read
                        { operation with
                            Work =
                                fun tx ->
                                    task {
                                        observations.Add tx.ObservedAt
                                        return! operation.Work tx
                                    }
                        }
            }

        let observedOwner = { owner with Store = observedStore }
        let! first = candidates observedOwner caseId None 1
        let first = read first
        Expect.equal observations.Count 1 "Candidate paging uses one authorized snapshot"

        Expect.equal
            first.Basis.ObservedAt
            observations[0]
            "Observation is the exact picker transaction instant"

        Expect.equal
            first.Basis.BusinessDate
            (fixture.Calendar.DateAt observations[0])
            "Business date comes from the installation calendar"

        Expect.equal
            first.Basis.TimeZoneId
            fixture.Calendar.TimeZoneId
            "Installation zone is retained"

        Expect.equal
            (first.Items |> List.map _.ActorId)
            [ CaseFixtures.actorId (id 1) ]
            "First bounded eligible human"

        Expect.isSome first.NextCursor "The second eligible account remains discoverable"
        let! second = candidates owner caseId first.NextCursor 1
        let second = read second

        Expect.equal
            second.Items
            [
                {
                    ActorId = CaseFixtures.actorId (id 10)
                    DisplayName =
                        DisplayName.Named(
                            PartyName.parse Inputs.actorProfile.DisplayName
                            |> Result.defaultWith (string >> failtest)
                        )
                    PrincipalKind = PrincipalKind.Human
                    Availability = ActorAvailability.Available
                }
            ]
            "Only purpose-limited account identity is disclosed"

        Expect.isNone second.NextCursor "No reader, service or unrelated account is included"
        let! wrongScope = candidates owner otherId first.NextCursor 1

        Expect.equal
            wrongScope
            (Outcome.Rejected Refusal.InvalidCursor)
            "Candidate cursors bind the actual case"

        for principal in [ reader; automation ] do
            let! denied = candidates (core fixture principal) caseId None 10

            Expect.equal
                denied
                (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
                "Reading a case cannot reveal an assignee directory"

        let! readerCase = get (core fixture reader) caseId
        Expect.isFalse (read readerCase).Mine "A scoped read grant is not responsibility"
        let! context = Service.context owner () token
        let today = (Inputs.activeContext (read context)).Runtime.Basis.BusinessDate
        let tomorrow = CalendarDate.ofDate (today.AddDays 1) |> CalendarDate.format

        let update: WorkOperationRequest =
            {
                OperationId = id 30
                Target = CaseSelector.Id caseId
                ExpectedRevision = 0L
                Act =
                    WorkAct.UpdateWork
                        {
                            Responsible =
                                WorkValue.Set(ActorId.format (CaseFixtures.actorId (id 10)))
                            Summary = WorkValue.Set "Awaiting evidence"
                        }
            }

        let! assigned = Service.execute owner (OperationInput.Work update) token
        let receipt = accepted assigned

        match receipt.Subject with
        | ReceiptSubject.Work work ->
            Expect.equal (Revision.value work.Revision) 1L "First independent work edit"
        | _ -> failtest "Expected work receipt"

        let! planned =
            Service.execute
                owner
                (OperationInput.Reminder
                    {
                        OperationId = id 130
                        Target = CaseSelector.Id caseId
                        Act =
                            AuthoredReminderAct.Create
                                {
                                    Action = "Request evidence"
                                    Due = tomorrow
                                }
                    })
                token

        accepted planned |> ignore
        let! current = get owner caseId
        let current = read current

        Expect.equal
            (Revision.value current.Snapshot.Revision)
            1L
            "Work and reminder writes preserve fact revision"

        Expect.equal
            (Revision.value current.Work.Revision)
            1L
            "Reminder does not advance work revision"

        Expect.equal current.Work.Due CaseDue.Upcoming "Future planned reminder is supported"
        Expect.equal current.Work.PendingReminders 1L "Reminder units"

        Expect.equal
            (current.Work.Responsible
             |> Option.map (fun value ->
                 value.ActorId, (value.Availability = ActorAvailability.Available)))
            (Some(CaseFixtures.actorId (id 10), true))
            "Relation-scoped identity and eligibility"

        let! mine =
            list
                (core fixture person)
                { CaseListFilter.empty with
                    Assignment = Some CaseAssignment.Mine
                }

        Expect.equal
            ((read mine).Items |> List.map _.CaseId)
            [ caseId ]
            "Mine follows responsibility inside access"

        let! notMine =
            list
                (core fixture reader)
                { CaseListFilter.empty with
                    Assignment = Some CaseAssignment.Mine
                }

        Expect.isEmpty (read notMine).Items "Read grant does not assign"

        let scopedQueue: ReminderListRequest =
            {
                Target = Some(CaseSelector.Id caseId)
                State = Some ReminderState.Pending
                Due = None
                Assignment = None
                Text = None
                AfterCursor = None
                Limit = 50
                View = None
                IncludeInactive = None
            }

        for target in [ Some(CaseSelector.Id caseId); None ] do
            let! queue =
                Service.reminders (core fixture person) { scopedQueue with Target = target } token

            let queue = read queue
            Expect.equal queue.Items.Length 1 "Scoped-only current reminder visibility"

            Expect.contains
                queue.Items.Head.Acts
                ReminderActKind.CompleteAndPlanNext
                "Scoped current work authority supplies actions"

        let! invisiblePage =
            list
                (core fixture person)
                { CaseListFilter.empty with
                    Text = Some "Nothing matches this filter"
                }

        Expect.isEmpty (read invisiblePage).Items "Independent page filter control"

        let! scopedOverview =
            Service.workOverview (core fixture person) { Mine = false } token

        Expect.equal
            (read scopedOverview).ActionableCases
            1L
            "Overview ignores text page filter but obeys scope"

        let! mineOverview = Service.workOverview (core fixture person) { Mine = true } token

        Expect.equal
            (read mineOverview).ActionableCases
            1L
            "Mine inherits current case responsibility"

        let authorityOnly =
            PrincipalKey.human "https://issuer.example/" "authority-only-owner"
            |> Result.defaultWith (string >> failtest)

        let! registeredOwner =
            Service.execute
                owner
                (OperationInput.Authority(
                    authorityRequest
                        (id 13)
                        authorityOnly
                        (AuthorityAct.Register ClaimCore.TestSupport.Inputs.actorProfile)
                ))
                token

        acceptedAuthority registeredOwner

        let! ownerGrant =
            grant
                owner
                (id 25)
                authorityOnly
                {
                    Role = Role.Owner
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority ownerGrant

        let! noCounts =
            Service.workOverview (core fixture authorityOnly) { Mine = false } token

        Expect.equal
            noCounts
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Authority-only role learns no case counts"

        let pending =
            OperationInput.Work
                { update with
                    OperationId = id 31
                    ExpectedRevision = 1L
                    Act =
                        WorkAct.UpdateWork
                            {
                                Responsible =
                                    WorkValue.Set(ActorId.format (CaseFixtures.actorId (id 10)))
                                Summary = WorkValue.Set "Reviewed draft"
                            }
                }

        let! preview = Service.preview owner pending token

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed _) -> ()
        | other -> failtestf "Preview control: %A" other

        let! revoked =
            grant
                owner
                (id 32)
                person
                {
                    Role = Role.CaseHandler
                    Scope = GrantTarget.CaseId caseId
                }
                false

        acceptedAuthority revoked
        let! refused = Service.execute owner pending token

        Expect.equal
            refused.Outcome
            (ExecuteResult.Rejected(Refusal.Case CaseRefusal.AssigneeUnavailable))
            "Action rechecks selected assignee after preview"

        let! retained = get owner caseId
        let retained = read retained

        Expect.equal
            (Revision.value retained.Snapshot.Revision)
            1L
            "No financial revision from refusal or revocation"

        Expect.equal (Revision.value retained.Work.Revision) 1L "No work revision from refusal"

        Expect.equal
            (retained.Work.Responsible
             |> Option.map (fun value -> value.Availability = ActorAvailability.Available))
            (Some false)
            "Unavailable relation remains factual"

        let! hidden = get (core fixture person) caseId

        Expect.equal
            hidden
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Assignment cannot restore revoked access"

        let! stale =
            Service.execute owner (OperationInput.Work { update with OperationId = id 33 }) token

        match stale.Outcome with
        | ExecuteResult.Rejected(Refusal.RevisionConflict conflict) ->
            Expect.equal
                (Revision.value conflict.ActualRevision)
                1L
                "Independent work revision conflict"

            Expect.isSome conflict.Changes "The authorized snapshot supplies conflict history"
        | other -> failtestf "Expected revision conflict, got %A" other

        for principal in [ reader; automation ] do
            let! denied =
                Service.execute
                    (core fixture principal)
                    (OperationInput.Work
                        { update with
                            OperationId = id 34
                            ExpectedRevision = 1L
                            Act =
                                WorkAct.UpdateWork
                                    {
                                        Responsible = WorkValue.Clear
                                        Summary = WorkValue.Keep
                                    }
                        })
                    token

            Expect.isFalse
                (match denied.Outcome with
                 | ExecuteResult.Accepted _ -> true
                 | _ -> false)
                "Reader/service cannot manage work"

        let! disabled =
            Service.execute
                owner
                (OperationInput.Authority(authorityRequest (id 35) person AuthorityAct.Disable))
                token

        acceptedAuthority disabled
        let! afterDisable = get owner caseId

        Expect.equal
            (read afterDisable).Work.Revision
            retained.Work.Revision
            "Disable changes no authored work revision"

        let! cleared =
            Service.execute
                owner
                (OperationInput.Work
                    { update with
                        OperationId = id 36
                        ExpectedRevision = 1L
                        Act =
                            WorkAct.UpdateWork
                                {
                                    Responsible = WorkValue.Clear
                                    Summary = WorkValue.Keep
                                }
                    })
                token

        accepted cleared |> ignore
        let! clearedView = get owner caseId
        Expect.isNone (read clearedView).Work.Responsible "Clear assignment explicitly"

        Expect.equal
            ((read clearedView).Work.Summary |> Option.map HandlingSummary.format)
            (Some "Awaiting evidence")
            "Unrelated summary retained"

        Expect.equal
            (read clearedView).Work.PendingReminders
            1L
            "Assignment does not rewrite reminders"
    }

let internal dueFiltersAndOrder fixture =
    task {
        let! owner = caseWorker fixture
        let! context = Service.context owner () token
        let context = Inputs.activeContext (read context)
        let today = context.Runtime.Basis.BusinessDate

        let date offset =
            today.AddDays offset |> CalendarDate.ofDate |> CalendarDate.format

        let add operation reference text due =
            Service.execute
                owner
                (OperationInput.Reminder
                    {
                        OperationId = id operation
                        Target = CaseSelector.Reference reference
                        Act = AuthoredReminderAct.Create { Action = text; Due = due }
                    })
                token

        for operation, reference in
            [
                50, "WORK-OVERDUE"
                51, "WORK-TODAY"
                52, "WORK-UPCOMING"
                53, "WORK-UNSCHEDULED"
            ] do
            let facts =
                if operation = 50 then
                    Inputs.registrationWith [ BusinessField.ClaimedAmount, "500.00" ] registration
                else
                    registration

            let! reply = execute owner operation reference 0L (CaseAct.Open facts)
            accepted reply |> ignore

        for operation, reference, text, due in
            [
                60, "WORK-OVERDUE", "Overdue action", date -1
                61, "WORK-TODAY", "Today action", date 0
                62, "WORK-UPCOMING", "Future action", date 1
                63, "WORK-OVERDUE", "Today second action", date 0
            ] do
            let! reply = add operation reference text due
            accepted reply |> ignore

        let! assigned =
            Service.execute
                owner
                (OperationInput.Work
                    {
                        OperationId = id 64
                        Target = CaseSelector.Reference "WORK-TODAY"
                        ExpectedRevision = 0L
                        Act =
                            WorkAct.UpdateWork
                                {
                                    Responsible = WorkValue.Set(ActorId.format context.ActorId)
                                    Summary = WorkValue.Keep
                                }
                    })
                token

        accepted assigned |> ignore

        for due, expected in
            [
                CaseDue.Overdue, "WORK-OVERDUE"
                CaseDue.Today, "WORK-TODAY"
                CaseDue.Upcoming, "WORK-UPCOMING"
                CaseDue.Unscheduled, "WORK-UNSCHEDULED"
            ] do
            let! page =
                list
                    owner
                    { CaseListFilter.empty with
                        Due = Some due
                    }

            Expect.equal
                ((read page).Items |> List.map (fun row -> CaseReference.format row.Reference))
                [ expected ]
                "Case minimum classification is mutually exclusive"

        let request: ReminderListRequest =
            {
                Target = None
                State = Some ReminderState.Pending
                Due = Some ReminderDue.Today
                Assignment = None
                Text = None
                AfterCursor = None
                Limit = 1
                View = None
                IncludeInactive = None
            }

        let! first = Service.reminders owner request token
        let first = read first
        Expect.equal first.Items.Length 1 "Bounded queue"
        Expect.isSome first.NextCursor "Second today reminder exists on another/overdue case"

        let! second =
            Service.reminders
                owner
                { request with
                    AfterCursor = first.NextCursor
                }
                token

        let second = read second
        Expect.equal second.Items.Length 1 "Next keyset page"

        for page in [ first; second ] do
            Expect.equal
                page.Items.Head.Timing
                (Some ReminderDue.Today)
                "Each continuation classifies its item against that response's date basis"

        Expect.notEqual
            (Reminder.id first.Items.Head.Reminder)
            (Reminder.id second.Items.Head.Reminder)
            "Stable tie identities"

        Expect.isNone second.NextCursor "Exactly two individual today actions"
        let! summary = Service.caseSummary owner CaseListFilter.empty token
        let summary = read summary
        Expect.equal summary.Counts.Total 4L "Reminder fanout cannot duplicate cases"

        Expect.equal
            (summary.Claimed
             |> List.map (fun total ->
                 Currency.format total.Currency, DecimalTotal.format total.Amount))
            [ ("EUR", "3500") ]
            "EUR500 case with two reminders contributes once"

        let! overview = Service.workOverview owner { Mine = false } token
        let overview = read overview
        Expect.equal overview.ActionableCases 4L "Global scope"
        Expect.equal overview.MissingPlanCases 1L "No placeholders"
        Expect.equal overview.OverdueCases 1L "Case units"
        Expect.equal overview.OverdueReminders 1L "Reminder units"
        Expect.equal overview.UnassignedCases 3L "Explicit responsibility"

        Expect.equal
            overview.AttentionCases
            4L
            "The distinct union includes the available assignee’s due-today case"

        let! completed =
            Service.execute
                owner
                (OperationInput.Reminder
                    {
                        OperationId = id 65
                        Target = CaseSelector.Reference "WORK-TODAY"
                        Act = AuthoredReminderAct.Complete(id 61, 1L)
                    })
                token

        accepted completed |> ignore
        let! after = Service.workOverview owner { Mine = false } token

        Expect.equal
            (read after).MissingPlanCases
            2L
            "Last completion accepts and creates monitored attention"

        let! invalid =
            Service.execute
                owner
                (OperationInput.Reminder
                    {
                        OperationId = id 66
                        Target = CaseSelector.Reference "WORK-OVERDUE"
                        Act =
                            AuthoredReminderAct.CompleteAndPlanNext(
                                id 60,
                                1L,
                                { Action = " "; Due = date 1 }
                            )
                    })
                token

        Expect.isFalse
            (match invalid.Outcome with
             | ExecuteResult.Accepted _ -> true
             | _ -> false)
            "Invalid compound refuses"

        let! pending =
            Service.reminders
                owner
                { request with
                    Target = Some(CaseSelector.Reference "WORK-OVERDUE")
                    Due = None
                    Limit = 50
                }
                token

        Expect.equal
            ((read pending).Items |> List.map (fun row -> Reminder.state row.Reminder))
            [ ReminderState.Pending; ReminderState.Pending ]
            "No partial completion"
    }

let internal reminderTiming fixture =
    task {
        let! initialOwner = caseWorker fixture

        let! stewardship =
            grant
                initialOwner
                (id 699)
                fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority stewardship
        let! context = Service.context initialOwner () token

        let initialDate = (Inputs.activeContext (read context)).Runtime.Basis.BusinessDate

        let mutable businessDate = initialDate

        let calendar =
            { new IBusinessCalendar with
                member _.TimeZoneId = fixture.Calendar.TimeZoneId
                member _.DateAt _ = businessDate
            }

        let owner = core { fixture with Calendar = calendar } fixture.Owner
        let reference = "REMINDER-TIMING"
        let target = CaseSelector.Reference reference
        let! opened = execute owner 700 reference 0L (CaseAct.Open registration)
        let caseId = accepted opened |> caseReceipt |> _.CaseId

        for operation, offset in [ 701, -1; 702, 0; 703, 1; 704, 0; 705, 0 ] do
            let! created =
                Service.execute
                    owner
                    (OperationInput.Reminder
                        {
                            OperationId = id operation
                            Target = target
                            Act =
                                AuthoredReminderAct.Create
                                    {
                                        Action = sprintf "Timing action %d" operation
                                        Due =
                                            initialDate.AddDays offset
                                            |> CalendarDate.ofDate
                                            |> CalendarDate.format
                                    }
                        })
                    token

            accepted created |> ignore

        for operation, act in
            [
                706, AuthoredReminderAct.Complete(id 704, 1L)
                707, AuthoredReminderAct.Cancel(id 705, 1L)
            ] do
            let! changed =
                Service.execute
                    owner
                    (OperationInput.Reminder
                        {
                            OperationId = id operation
                            Target = target
                            Act = act
                        })
                    token

            accepted changed |> ignore

        let request: ReminderListRequest =
            {
                Target = Some target
                State = None
                Due = None
                Assignment = None
                Text = None
                AfterCursor = None
                Limit = 50
                View = None
                IncludeInactive = None
            }

        let single operation =
            task {
                let! response =
                    Service.reminder
                        owner
                        {
                            Target = target
                            ReminderId = id operation
                        }
                        token

                let response = read response

                Expect.equal
                    response.Basis.BusinessDate
                    businessDate
                    "Single timing uses its actual installation business date"

                Expect.equal
                    response.Basis.TimeZoneId
                    calendar.TimeZoneId
                    "Single observation identifies installation zone"

                return response.Item
            }

        let checkPage query expected =
            task {
                let! response = Service.reminders owner query token
                let response = read response

                Expect.equal
                    response.Basis.BusinessDate
                    businessDate
                    "Page timing uses its actual installation business date"

                Expect.equal
                    response.Basis.TimeZoneId
                    calendar.TimeZoneId
                    "Page observation identifies installation zone"

                Expect.equal
                    (response.Items
                     |> List.map (fun item ->
                         Reminder.id item.Reminder |> ReminderId.value, item.Timing)
                     |> Map.ofList)
                    (expected
                     |> List.map (fun (operation, timing) -> id operation, timing)
                     |> Map.ofList)
                    "Independent expected current timing for each selected reminder"

                return response
            }

        let expected =
            [
                701, Some ReminderDue.Overdue
                702, Some ReminderDue.Today
                703, Some ReminderDue.Upcoming
                704, None
                705, None
            ]

        for operation, timing in expected do
            let! item = single operation
            Expect.isTrue item.Active "An open parent remains active even for terminal reminders"

            Expect.equal
                item.Timing
                timing
                "Single read distinguishes pending timing from terminal state"

        let pendingExpected = expected |> List.take 3
        let! _ = checkPage request pendingExpected
        let! _ = checkPage { request with Target = None } pendingExpected

        for state, operation in [ ReminderState.Completed, 704; ReminderState.Cancelled, 705 ] do
            let explicitState = { request with State = Some state }
            let! _ = checkPage explicitState [ operation, None ]
            let! _ = checkPage { explicitState with Target = None } [ operation, None ]
            ()

        let! first =
            checkPage
                { request with
                    State = Some ReminderState.Pending
                    Due = Some ReminderDue.Today
                    Limit = 1
                }
                [ 702, Some ReminderDue.Today ]

        Expect.isNone
            first.NextCursor
            "Terminal reminders matching the date are excluded by the pending-state filter"

        let stored () =
            fixture.Store.Read
                {
                    CancellationToken = token
                    Work =
                        fun tx ->
                            task {
                                let! activity =
                                    tx.ActivityPage
                                        {
                                            CaseId = caseId
                                            BeforeSequence = None
                                            Limit = 50
                                        }

                                let! reminder =
                                    tx.FindReminder(ReminderId.create (id 702) |> Option.get)

                                return activity, reminder
                            }
                }

        let! beforePassage = stored ()
        businessDate <- initialDate.AddDays 1
        let! yesterday = single 702

        Expect.equal
            yesterday.Timing
            (Some ReminderDue.Overdue)
            "Date passage reclassifies retained pending work"

        let! _ =
            checkPage
                { request with
                    State = Some ReminderState.Pending
                    Due = Some ReminderDue.Today
                }
                [ 703, Some ReminderDue.Today ]

        let! afterPassage = stored ()

        Expect.equal
            afterPassage
            beforePassage
            "Read-time reclassification writes neither reminder state nor ledger activity"

        let! closed = execute owner 708 reference 1L (CaseAct.Bare BareAct.Close)
        accepted closed |> ignore
        let inactive = pendingExpected |> List.map (fun (operation, _) -> operation, None)
        let! hidden = checkPage request []
        Expect.isNone hidden.NextCursor "Inactive work does not leak through pagination"

        let! _ =
            checkPage
                { request with
                    IncludeInactive = Some true
                }
                inactive

        let! _ =
            checkPage
                { request with
                    Target = None
                    IncludeInactive = Some true
                }
                inactive

        let! _ =
            checkPage
                { request with
                    IncludeInactive = Some true
                    State = Some ReminderState.Completed
                    Due = Some ReminderDue.Overdue
                }
                [ 704, None ]

        let! closedItem = single 702
        Expect.isFalse closedItem.Active "Closed single read clearly marks retained work inactive"
        Expect.isNone closedItem.Timing "Closed pending work has no actionable timing"

        let! reopened = execute owner 709 reference 2L (CaseAct.Bare BareAct.Reopen)
        accepted reopened |> ignore
        let! reopenedItem = single 702
        Expect.isTrue reopenedItem.Active "Reopening reactivates retained pending work"

        Expect.equal
            reopenedItem.Timing
            (Some ReminderDue.Overdue)
            "Reopened work is classified against the current date"

        Expect.equal
            (Reminder.due reopenedItem.Reminder)
            (Reminder.due yesterday.Reminder)
            "Reopening retains the authored due date"

        let! voided = execute owner 710 reference 3L (CaseAct.Void "Timing lifecycle check")
        accepted voided |> ignore

        let! _ =
            checkPage
                { request with
                    IncludeInactive = Some true
                }
                inactive

        let! voidedItem = single 702
        Expect.isFalse voidedItem.Active "Voided single read marks retained work inactive"
        Expect.isNone voidedItem.Timing "Voided pending work has no actionable timing"

        let! reinstated =
            execute owner 711 reference 4L (CaseAct.Reinstate "Restore timing lifecycle check")

        accepted reinstated |> ignore
        let! reinstatedItem = single 702
        Expect.isTrue reinstatedItem.Active "Reinstatement reactivates work"

        Expect.equal
            reinstatedItem.Timing
            (Some ReminderDue.Overdue)
            "Reinstatement uses retained dates and current basis"
    }

let internal lifecycleAndErasure fixture =
    task {
        let! owner = caseWorker fixture

        let! stewardship =
            grant
                owner
                (id 19)
                fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority stewardship

        let facts =
            Inputs.registrationWith
                [ BusinessField.IncidentDescription, "Erase incident narrative" ]
                registration

        let! opened = execute owner 20 "WORK-LIFECYCLE" 0L (CaseAct.Open facts)
        let caseId = accepted opened |> caseReceipt |> _.CaseId
        let target = CaseSelector.Id caseId

        let! work =
            Service.execute
                owner
                (OperationInput.Work
                    {
                        OperationId = id 21
                        Target = target
                        ExpectedRevision = 0L
                        Act =
                            WorkAct.UpdateWork
                                {
                                    Responsible =
                                        WorkValue.Set(ActorId.format (CaseFixtures.actorId (id 1)))
                                    Summary = WorkValue.Set "Erase handling summary"
                                }
                    })
                token

        accepted work |> ignore

        let add operation text =
            Service.execute
                owner
                (OperationInput.Reminder
                    {
                        OperationId = id operation
                        Target = target
                        Act = AuthoredReminderAct.Create { Action = text; Due = "2027-12-31" }
                    })
                token

        for operation, text in
            [
                22, "Erase pending action"
                23, "Erase completed action"
                24, "Erase cancelled action"
            ] do
            let! reply = add operation text
            accepted reply |> ignore

        for operation, act in
            [
                25, AuthoredReminderAct.Complete(id 23, 1L)
                26, AuthoredReminderAct.Cancel(id 24, 1L)
            ] do
            let! reply =
                Service.execute
                    owner
                    (OperationInput.Reminder
                        {
                            OperationId = id operation
                            Target = target
                            Act = act
                        })
                    token

            accepted reply |> ignore

        let! closed = execute owner 27 "WORK-LIFECYCLE" 1L (CaseAct.Bare BareAct.Close)
        accepted closed |> ignore

        let! summary =
            Service.execute
                owner
                (OperationInput.Work
                    {
                        OperationId = id 28
                        Target = target
                        ExpectedRevision = 1L
                        Act =
                            WorkAct.UpdateWork
                                {
                                    Responsible = WorkValue.Keep
                                    Summary = WorkValue.Set "Closed handling summary"
                                }
                    })
                token

        accepted summary |> ignore
        let! refused = add 29 "Closed cannot plan"

        Expect.isFalse
            (match refused.Outcome with
             | ExecuteResult.Accepted _ -> true
             | _ -> false)
            "Closed parent rejects creation"

        let! inactive = get owner caseId
        Expect.equal (read inactive).Work.Due CaseDue.Inactive "Closed work inactive"

        Expect.equal
            (read inactive).Work.PendingReminders
            1L
            "Closing did not complete pending work"

        let! reopened = execute owner 30 "WORK-LIFECYCLE" 2L (CaseAct.Bare BareAct.Reopen)
        accepted reopened |> ignore
        let! active = get owner caseId

        Expect.equal
            ((read active).Work.FollowUp |> Option.map CalendarDate.format)
            (Some "2027-12-31")
            "Reactivation retains exact date"

        let! compound =
            Service.execute
                owner
                (OperationInput.Reminder
                    {
                        OperationId = id 31
                        Target = target
                        Act =
                            AuthoredReminderAct.CompleteAndPlanNext(
                                id 22,
                                1L,
                                {
                                    Action = "Erase replacement action"
                                    Due = "2028-01-01"
                                }
                            )
                    })
                token

        let compoundReceipt = accepted compound

        let! replayed =
            Service.execute
                owner
                (OperationInput.Reminder
                    {
                        OperationId = id 31
                        Target = target
                        Act =
                            AuthoredReminderAct.CompleteAndPlanNext(
                                id 22,
                                1L,
                                {
                                    Action = "Erase replacement action"
                                    Due = "2028-01-01"
                                }
                            )
                    })
                token

        expectReceipt
            (replayReceipt replayed)
            compoundReceipt
            "Compound replay preserves both effects and identity"

        let! voided =
            execute owner 32 "WORK-LIFECYCLE" 3L (CaseAct.Void "Synthetic entry error")

        accepted voided |> ignore

        let! blocked =
            Service.execute
                owner
                (OperationInput.Reminder
                    {
                        OperationId = id 33
                        Target = target
                        Act = AuthoredReminderAct.Complete(id 31, 1L)
                    })
                token

        Expect.isFalse
            (match blocked.Outcome with
             | ExecuteResult.Accepted _ -> true
             | _ -> false)
            "Voided rejects ordinary reminders"

        let! restored =
            execute owner 34 "WORK-LIFECYCLE" 4L (CaseAct.Reinstate "Synthetic correction")

        accepted restored |> ignore
        let! erased = execute owner 35 "WORK-LIFECYCLE" 5L (CaseAct.Bare BareAct.Erase)
        accepted erased |> ignore
        let! current = get owner caseId
        Expect.equal (read current).Snapshot.State CaseState.Erased "Erased parent masks content"
        Expect.isNone (read current).Work.Summary "No current summary"
        Expect.isNone (read current).Work.FollowUp "No current due payload"

        let! stored =
            fixture.Store.Read
                {
                    CancellationToken = token
                    Work =
                        fun tx ->
                            task {
                                let! work = tx.FindWork caseId
                                let! a = tx.FindReminder(ReminderId.create (id 22) |> Option.get)
                                let! b = tx.FindReminder(ReminderId.create (id 23) |> Option.get)
                                let! c = tx.FindReminder(ReminderId.create (id 24) |> Option.get)
                                let! d = tx.FindReminder(ReminderId.create (id 31) |> Option.get)
                                let entries = ResizeArray<StoredEntry>()

                                for operation in
                                    [ 20; 21; 22; 23; 24; 25; 26; 27; 28; 30; 31; 32; 34; 35 ] do
                                    let! entry =
                                        tx.FindOperation(
                                            OperationId.create (id operation) |> Option.get
                                        )

                                    entries.AddRange((Option.get entry).Entries)

                                return work, [ a; b; c; d ], List.ofSeq entries
                            }
                }

        let work, reminders, entries =
            match stored with
            | Ok value -> value
            | Error other -> failtestf "Erasure storage observation: %A" other

        Expect.isNone (Option.get work).Snapshot "Work tombstone"
        Expect.isEmpty (Option.get work).SnapshotBytes "No work bytes"

        for reminder in reminders do
            let reminder = Option.get reminder
            Expect.isNone reminder.Snapshot "Reminder tombstone reserves ID"
            Expect.isEmpty reminder.SnapshotBytes "No reminder bytes"

        for entry in entries do
            Expect.isNone entry.Payload "All related retained payloads erased"
            Expect.isNone entry.PayloadNonce "No nonce retains erased payload"
    }

let internal partialUpdatesAndReaffirmation fixture =
    task {
        let! owner = caseWorker fixture
        let! opened = execute owner 40 "PARTIAL-FACTS" 0L (CaseAct.Open registration)
        let caseId = accepted opened |> caseReceipt |> _.CaseId

        let partial: RegistrationUpdateInput =
            {
                IncidentDate = None
                NotificationDate = None
                Country = None
                Claimant = Some "Only selected claimant"
                Insurer = None
                Claimed = None
                IncidentDescription = WorkValue.Keep
            }

        let! edited = execute owner 41 "PARTIAL-FACTS" 1L (CaseAct.AmendRegistration partial)
        let fields = disclosedFields (accepted edited)

        Expect.equal
            (PartyName.format (Registration.claimant fields.Registration))
            "Only selected claimant"
            "One asserted fact"

        Expect.equal
            (PartyName.format (Registration.insurer fields.Registration))
            "Synthetic Insurer"
            "Omitted insurer retained"

        Expect.equal
            (Money.format (Registration.claimed fields.Registration))
            "1000"
            "Omitted money retains its complete pair"

        let! stale =
            execute
                owner
                42
                "PARTIAL-FACTS"
                1L
                (CaseAct.AmendRegistration
                    { partial with
                        Claimant = Some "Stale edit"
                    })

        match stale.Outcome with
        | ExecuteResult.Rejected(Refusal.RevisionConflict conflict) ->
            Expect.equal (Revision.value conflict.ActualRevision) 2L "No automatic partial rebase"
            Expect.isSome conflict.Changes "The authorized snapshot supplies conflict history"
        | other -> failtestf "Expected revision conflict, got %A" other

        let! decided = execute owner 43 "PARTIAL-FACTS" 2L (CaseAct.Decide Inputs.decision)
        accepted decided |> ignore

        let! paid =
            execute owner 44 "PARTIAL-FACTS" 3L (CaseAct.RecordPayment Inputs.payment)

        accepted paid |> ignore

        let change: AuthoredCorrection =
            {
                Registration = Amendment.Keep
                Decision =
                    Change.Update
                        {
                            Date = None
                            Payable = Some { Amount = "650.00"; Currency = "USD" }
                        }
                Payment = PaymentChange.Keep
            }

        let! acknowledgement = execute owner 45 "PARTIAL-FACTS" 4L (CaseAct.Correct change)

        Expect.equal
            acknowledgement.Outcome
            (ExecuteResult.Rejected(
                Refusal.invalidInput (
                    InputTarget.Group RecordGroup.Payment,
                    Violation.PaymentAcknowledgementRequired
                )
            ))
            "No silent payment reuse"

        let reaffirm =
            CaseAct.Correct
                { change with
                    Payment = PaymentChange.Reaffirm
                }

        let! revoked =
            grant
                owner
                (id 46)
                fixture.Owner
                {
                    Role = Role.PaymentRecorder
                    Scope = GrantTarget.Installation
                }
                false

        acceptedAuthority revoked
        let! denied = execute owner 47 "PARTIAL-FACTS" 4L reaffirm

        Expect.equal
            denied.Outcome
            (ExecuteResult.Rejected(
                Refusal.Access(AccessRefusal.NotAuthorized Capability.RecordPayment)
            ))
            "Reaffirmation requires the payment capability"

        let! granted =
            grant
                owner
                (id 48)
                fixture.Owner
                {
                    Role = Role.PaymentRecorder
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority granted
        let! updated = execute owner 49 "PARTIAL-FACTS" 4L reaffirm
        let updated = disclosedFields (accepted updated)

        Expect.equal
            (updated.Payment |> Option.map (Payment.date >> CalendarDate.format))
            (Some "2026-08-20")
            "Reaffirmation retains the existing actual date"

        Expect.equal
            (updated.Decision |> Option.map (Decision.date >> CalendarDate.format))
            (Some "2026-08-15")
            "Omitted decision date retained"

        Expect.equal
            (updated.Decision |> Option.map (Decision.payable >> Money.currency))
            (Some "USD")
            "Complete replacement money pair"

        let! chronology =
            execute
                owner
                50
                "PARTIAL-FACTS"
                5L
                (CaseAct.Correct
                    { change with
                        Decision =
                            Change.Update
                                {
                                    Date = Some "2026-08-21"
                                    Payable = None
                                }
                        Payment = PaymentChange.Reaffirm
                    })

        match chronology.Outcome with
        | ExecuteResult.Rejected(Refusal.InvalidInput issues) ->
            let issues = InputIssues.toList issues
            Expect.equal issues.Length 1 "One invalid relation"

            Expect.equal
                issues.Head.Target
                (InputTarget.Field BusinessField.PaymentDecisionDate)
                "The asserted side is actionable"

            Expect.equal
                (issues.Head.RelatedDate
                 |> Option.map (fun date -> date.Field, CalendarDate.format date.Value))
                (Some(BusinessField.PaymentDate, "2026-08-20"))
                "Retained counterpart context"
        | other -> failtestf "Expected chronology refusal: %A" other

        let! retained = get owner caseId

        Expect.equal
            (read retained).Snapshot.Revision
            (CaseFixtures.revision 5L)
            "Chronology refusal cannot create a revision or silently rebase"
    }

let internal finiteDateRange fixture =
    task {
        let! owner = caseWorker fixture

        for number, reference in [ 600, "DATE-MIN"; 601, "DATE-MAX-A"; 602, "DATE-MAX-B" ] do
            let! opened = execute owner number reference 0L (CaseAct.Open registration)
            accepted opened |> ignore

        for number, reference, due in
            [
                610, "DATE-MIN", "0001-01-01"
                611, "DATE-MIN", "9999-12-31"
                612, "DATE-MIN", "9999-12-31"
                613, "DATE-MAX-A", "9999-12-31"
                614, "DATE-MAX-B", "9999-12-31"
            ] do
            let! planned =
                Service.execute
                    owner
                    (OperationInput.Reminder
                        {
                            OperationId = id number
                            Target = CaseSelector.Reference reference
                            Act =
                                AuthoredReminderAct.Create
                                    {
                                        Action = "Finite Gregorian date"
                                        Due = due
                                    }
                        })
                    token

            accepted planned |> ignore

        let request: ReminderListRequest =
            {
                Target = Some(CaseSelector.Reference "DATE-MIN")
                State = Some ReminderState.Pending
                Due = None
                Assignment = None
                Text = None
                AfterCursor = None
                Limit = 1
                View = None
                IncludeInactive = None
            }

        let mutable cursor = None
        let dates = ResizeArray<string>()

        for _ in 1..3 do
            let! response = Service.reminders owner { request with AfterCursor = cursor } token
            let page = read response
            Expect.equal page.Items.Length 1 "Exactly one bounded item per extreme-date page"
            dates.Add(Reminder.due page.Items.Head.Reminder |> CalendarDate.format)
            cursor <- page.NextCursor

        Expect.equal
            (List.ofSeq dates)
            [ "0001-01-01"; "9999-12-31"; "9999-12-31" ]
            "Finite dates survive both writes and continuation parameters"

        Expect.isNone cursor "All independently seeded reminders discovered"
        let mutable caseCursor = None
        let references = ResizeArray<string>()

        for _ in 1..3 do
            let! response =
                Service.list
                    owner
                    {
                        Page = { AfterCursor = caseCursor; Limit = 1 }
                        Filter =
                            { CaseListFilter.empty with
                                Order = CaseListOrder.FollowUp
                            }
                    }
                    token

            let page = read response
            Expect.equal page.Items.Length 1 "Follow-up pages preserve exact finite boundaries"
            references.Add(CaseReference.format page.Items.Head.Reference)
            caseCursor <- page.NextCursor

        Expect.equal
            (List.ofSeq references)
            [ "DATE-MIN"; "DATE-MAX-A"; "DATE-MAX-B" ]
            "No infinity conflation in derived case keysets"

        Expect.isNone caseCursor "No duplicate case at maximum date"
    }

let internal pairedPage fixture =
    task {
        let! owner = caseWorker fixture

        for operation, reference in [ 1100, "PAGE-A"; 1101, "PAGE-B"; 1102, "PAGE-C" ] do
            let! opened = execute owner operation reference 0L (CaseAct.Open registration)
            accepted opened |> ignore

        let! updated =
            Service.execute
                owner
                (OperationInput.Work
                    {
                        OperationId = id 1103
                        Target = CaseSelector.Reference "PAGE-A"
                        ExpectedRevision = 0L
                        Act =
                            WorkAct.UpdateWork
                                {
                                    Summary = WorkValue.Set "Page-owned summary\nالعربية"
                                    Responsible =
                                        WorkValue.Set(ActorId.format (CaseFixtures.actorId (id 1)))
                                }
                    })
                token

        accepted updated |> ignore

        for operation in 1110..1119 do
            let! created =
                Service.execute
                    owner
                    (OperationInput.Reminder
                        {
                            OperationId = id operation
                            Target = CaseSelector.Reference "PAGE-A"
                            Act =
                                AuthoredReminderAct.Create
                                    {
                                        Action =
                                            if operation = 1110 then
                                                "First pending action"
                                            else
                                                "Later pending action"
                                        Due =
                                            if operation = 1110 then "0001-01-01" else "9999-12-31"
                                    }
                        })
                    token

            accepted created |> ignore

        for order in [ CaseListOrder.Reference; CaseListOrder.Notified ] do
            let request =
                {
                    CaseListRequest.Page = { AfterCursor = None; Limit = 2 }
                    Filter =
                        { CaseListFilter.empty with
                            Order = order
                        }
                }

            let! response = Service.list owner request token
            let page = read response

            Expect.equal
                (page.Items |> List.map (_.Reference >> CaseReference.format))
                [ "PAGE-A"; "PAGE-B" ]
                "The parent limit applies before reminder fan-out"

            Expect.isSome page.NextCursor "The third parent supplies a continuation"
            let first = page.Items.Head

            let! detail =
                Service.get
                    owner
                    {
                        Target = CaseSelector.Id first.CaseId
                    }
                    token

            let detail = read detail
            let work = detail.Work

            Expect.equal
                first.Revision
                Revision.first
                "A work page retains the independent fact revision"

            Expect.equal
                work.Revision
                Revision.first
                "The detail retains its independent work revision"

            Expect.equal
                (work.Summary |> Option.map HandlingSummary.format)
                (Some "Page-owned summary\nالعربية")
                "The selected work owner supplies exact text"

            Expect.equal
                (first.PendingReminders, work.OverdueReminders)
                (10L, 1L)
                "Reminder counts have reminder units"

            Expect.equal
                (first.NextAction |> Option.map (fun next -> ReminderAction.format next.Action))
                (Some "First pending action")
                "The minimum pending date supplies the next action"

            Expect.equal
                (first.NextAction |> Option.map (fun next -> CalendarDate.format next.Due))
                (Some "0001-01-01")
                "The derived date is preserved at the Gregorian boundary"

            Expect.equal
                (first.Responsible
                 |> Option.map (fun account ->
                     account.ActorId, (account.Availability = ActorAvailability.Available)))
                (Some(CaseFixtures.actorId (id 1), true))
                "Assignment labels retain the case-scoped availability check"

            let empty = page.Items[1]

            Expect.equal
                (empty.NextAction, empty.Responsible, empty.PendingReminders)
                (None, None, 0L)
                "The next parent has no invented work values"

            let! continued =
                Service.list
                    owner
                    { request with
                        Page =
                            { request.Page with
                                AfterCursor = page.NextCursor
                            }
                    }
                    token

            let continued = read continued

            Expect.equal
                (continued.Items |> List.map (_.Reference >> CaseReference.format))
                [ "PAGE-C" ]
                "Continuation visits the remaining parent exactly once"

            Expect.isNone continued.NextCursor "All three parents have been visited"
    }
