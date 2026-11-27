module ClaimCore.IntegrationTests.ReminderPostgresTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.Application
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected successful current read: %A" other

let private parsed =
    function
    | Ok value -> value
    | Error issue -> failtestf "Expected representable retained value: %A" issue

let private target = CaseSelector.Reference populatedReference

let private reminder fixture operation taskName selected values =
    Submissions.createRaw
        taskName
        (id fixture operation)
        (OperationBinding.Reminder((target), selected))
        values

let private queue actor cursor =
    Service.reminders
        actor
        {
            Target = Some target
            State = None
            Kind = None
            DeadlineOutcome = None
            Due = None
            Assignment = None
            Text = None
            AfterCursor = cursor
            Limit = 1
            View = None
            IncludeInactive = None
        }
        token

let private deadlineKinds database =
    task {
        let! actor = populated database

        let create operation kind =
            let plan = Submissions.reminderPlan "Review synthetic evidence" "2026-11-16"

            let fields =
                kind
                |> Option.map (fun kind ->
                    Map.add
                        (Submissions.input InputTarget.ReminderPlanKind)
                        (SubmissionValue.Text kind)
                        plan)
                |> Option.defaultValue plan

            Service.executeSubmission
                actor
                (reminder database.Fixture operation "reminder.create" None fields)
                token

        let! followUp = create 701 None
        accepted followUp |> ignore
        let! deadline = create 702 (Some "DEADLINE")
        accepted deadline |> ignore

        let! completed =
            Service.executeSubmission
                actor
                (reminder
                    database.Fixture
                    703
                    "reminder.complete"
                    (Some(id database.Fixture 702, 1L))
                    (Map.ofList
                        [
                            Submissions.input InputTarget.ReminderDoneOn,
                            SubmissionValue.Text "2026-11-15"
                        ]))
                token

        accepted completed |> ignore

        let queue kind deadlineOutcome =
            Service.reminders
                actor
                {
                    Target = Some target
                    State = Some ReminderStateSelection.Any
                    Kind = kind
                    DeadlineOutcome = deadlineOutcome
                    Due = None
                    Assignment = None
                    Text = None
                    AfterCursor = None
                    Limit = 50
                    View = None
                    IncludeInactive = None
                }
                token

        let ids (result: Outcome<ReminderPage>) =
            (read result).Items
            |> List.map (fun row -> Reminder.id row.Reminder |> ReminderId.value)
            |> List.sort

        for filter, expected in
            [
                ReminderKindSelection.Any, [ 701; 702 ]
                ReminderKindSelection.Kind ReminderKind.FollowUp, [ 701 ]
                ReminderKindSelection.Kind ReminderKind.Deadline, [ 702 ]
            ] do
            let! result = queue (Some filter) None

            Expect.equal
                (ids result)
                (expected |> List.map (id database.Fixture) |> List.sort)
                "The PostgreSQL query maps the kind selector to its stored type"

        let! met = queue None (Some DeadlineOutcome.Met)

        Expect.equal
            (ids met)
            [ id database.Fixture 702 ]
            "Deadline outcomes reach the same filter plan"

        let! missed = queue None (Some DeadlineOutcome.Missed)
        Expect.isEmpty (ids missed) "The completion date is not evaluated as the recording date"

        let! steward =
            grant
                actor
                (id database.Fixture 920000)
                database.Fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople steward

        let! voided =
            Service.executeSubmission
                actor
                (request
                    database.Fixture
                    704
                    "synthetic-a"
                    1L
                    "case.void"
                    (Map.ofList
                        [
                            Submissions.input InputTarget.Reason,
                            SubmissionValue.Text "Synthetic record"
                        ]))
                token

        accepted voided |> ignore

        let! erased =
            Service.executeSubmission
                actor
                (request database.Fixture 705 "synthetic-a" 2L "case.erase" Map.empty)
                token

        accepted erased |> ignore

        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! shape =
            DatabaseSession.rows
                connection
                "SELECT count(*),count(*) FILTER (WHERE kind IS NOT NULL OR done_on IS NOT NULL) FROM claimcore.reminders WHERE case_id=$1"
                [| box (id database.Fixture 10) |]
                token

        Expect.equal
            (DatabaseSession.value<int64> 0 shape.Head)
            2L
            "Erasure kept both reminder identities"

        Expect.equal
            (DatabaseSession.value<int64> 1 shape.Head)
            0L
            "Erasure cleared the private columns in PostgreSQL"

        let! _ = verified database
        return ()
    }

let private dates database =
    task {
        let! actor = populated database

        for operation, date in [ 100, "0001-01-01"; 101, "9999-12-31" ] do
            let! created =
                Service.executeSubmission
                    actor
                    (reminder
                        (database.Fixture)
                        (operation)
                        "reminder.create"
                        (None)
                        (ClaimCore.TestSupport.Submissions.reminderPlan
                            ("Review the boundary date")
                            (date)))
                    token

            accepted created |> ignore

        let! first = queue actor None
        let first = read first

        Expect.equal
            (first.Items
             |> List.map (fun row -> Reminder.due row.Reminder |> CalendarDate.format))
            [ "0001-01-01" ]
            "Minimum Gregorian date is stored as a date, not provider negative infinity"

        Expect.isSome first.NextCursor "The queue has another pending reminder"
        let! second = queue actor first.NextCursor
        let second = read second

        Expect.equal
            (second.Items
             |> List.map (fun row -> Reminder.due row.Reminder |> CalendarDate.format))
            [ "9999-12-31" ]
            "Maximum Gregorian date survives actual PostgreSQL and a continuation"

        Expect.isNone second.NextCursor "The actual queue has exactly two items"

        let! current = Service.get actor { Target = target } token
        let today = (read current).Work.Basis.BusinessDate

        let! dueToday =
            Service.executeSubmission
                actor
                (reminder
                    (database.Fixture)
                    (105)
                    "reminder.create"
                    (None)
                    (ClaimCore.TestSupport.Submissions.reminderPlan
                        ("Due on the observed business date")
                        (CalendarDate.format (CalendarDate.ofDate today))))
                token

        accepted dueToday |> ignore
        let! beforeFilters = verified database

        for filter in [ ReminderDue.Overdue; ReminderDue.Today; ReminderDue.Upcoming ] do
            let! result =
                Service.reminders
                    actor
                    {
                        Target = Some target
                        State = None
                        Kind = None
                        DeadlineOutcome = None
                        Due = Some filter
                        Assignment = None
                        Text = None
                        AfterCursor = None
                        Limit = 50
                        View = None
                        IncludeInactive = None
                    }
                    token

            let result = read result
            // Dates are independently authored; use this result's own observation if midnight passed.
            let expected =
                [ 100, System.DateOnly.MinValue; 105, today; 101, System.DateOnly.MaxValue ]
                |> List.filter (fun (_, date) ->
                    match filter with
                    | ReminderDue.Overdue -> date < result.Basis.BusinessDate
                    | ReminderDue.Today -> date = result.Basis.BusinessDate
                    | ReminderDue.Upcoming -> date > result.Basis.BusinessDate)
                |> List.map (fun (operation, _) ->
                    ReminderId.ofOperationId (
                        CaseFixtures.operationId (id database.Fixture operation)
                    ))

            Expect.equal
                (result.Items |> List.map (fun item -> Reminder.id item.Reminder))
                expected
                "Each actual PostgreSQL reminder-date filter selects individual pending actions using its reported server date"

            for item in result.Items do
                Expect.equal
                    item.Timing
                    (Some filter)
                    "Actual PostgreSQL rows report the independently checked server-date bucket"

                let! single =
                    Service.reminder
                        actor
                        {
                            Target = target
                            ReminderId = Reminder.id item.Reminder |> ReminderId.value
                        }
                        token

                let single = read single
                let date = Reminder.due single.Item.Reminder |> CalendarDate.value

                let expectedTiming =
                    if date < single.Basis.BusinessDate then
                        Some ReminderDue.Overdue
                    elif date = single.Basis.BusinessDate then
                        Some ReminderDue.Today
                    else
                        Some ReminderDue.Upcoming

                Expect.equal
                    single.Item.Timing
                    expectedTiming
                    "Single current read uses its own actual server observation date"

        let! nextDue = Service.get actor { Target = target } token

        Expect.equal
            (read nextDue).Work.Due
            CaseDue.Overdue
            "An overdue minimum keeps the case overdue even when the reminder database.Fixture queue selects an individual today action"

        let! todayCases =
            Service.list
                actor
                {
                    Page = { AfterCursor = None; Limit = 50 }
                    Filter =
                        { CaseListFilter.empty with
                            Due = Some CaseDue.Today
                        }
                }
                token

        Expect.isEmpty
            (read todayCases).Items
            "Case next-due filtering uses the minimum pending date, independently of the individual-reminder database.Fixture today filter"

        let! afterFilters = verified database

        Expect.equal
            afterFilters
            beforeFilters
            "Read-time due classification never appends synthetic ledger events"

        let! cancelledToday =
            Service.executeSubmission
                actor
                (reminder
                    (database.Fixture)
                    (106)
                    "reminder.cancel"
                    (Some((id database.Fixture 105), (1L)))
                    (Map.empty))
                token

        accepted cancelledToday |> ignore

        let! cancelled =
            Service.executeSubmission
                actor
                (reminder
                    (database.Fixture)
                    (102)
                    "reminder.cancel"
                    (Some((id database.Fixture 100), (1L)))
                    (Map.empty))
                token

        accepted cancelled |> ignore

        let minimumRegistration =
            DomainInputs.registrationWith
                [
                    BusinessField.IncidentDate, "0001-01-01"
                    BusinessField.ReceivedDate, "0001-01-01"
                ]
                registration

        let! opened =
            Service.executeSubmission
                actor
                (((request
                    (database.Fixture)
                    (103)
                    ("synthetic-b")
                    (0L)
                    "case.open"
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values (minimumRegistration)
                    )))))
                token

        accepted opened |> ignore

        let! planned =
            Service.executeSubmission
                actor
                ((ClaimCore.TestSupport.Submissions.createRaw)
                    "reminder.create"
                    (id database.Fixture 104)
                    (ClaimCore.Application.Contracts.OperationBinding.Reminder(
                        (caseTarget database.Fixture "synthetic-b"),
                        None
                    ))
                    (ClaimCore.TestSupport.Submissions.reminderPlan
                        ("Another future action")
                        ("9999-12-31")))
                token

        accepted planned |> ignore

        let filter =
            { CaseListFilter.empty with
                Order = CaseListOrder.FollowUp
            }

        let! page =
            Service.list
                actor
                {
                    Page = { AfterCursor = None; Limit = 1 }
                    Filter = filter
                }
                token

        let page = read page
        Expect.isSome page.NextCursor "Equal maximum next dates need a reference tie continuation"

        let! continued =
            Service.list
                actor
                {
                    Page =
                        {
                            AfterCursor = page.NextCursor
                            Limit = 1
                        }
                    Filter = filter
                }
                token

        Expect.equal
            ((read continued).Items
             |> List.map (fun value -> caseAlias database.Fixture value.CaseId))
            [ "synthetic-b" ]
            "The case continuation passes the exact maximum date without infinity conversion"

        let notified =
            { CaseListFilter.empty with
                Order = CaseListOrder.Received
            }

        let! oldest =
            Service.list
                actor
                {
                    Page = { AfterCursor = None; Limit = 1 }
                    Filter = notified
                }
                token

        let oldest = read oldest

        Expect.equal
            (oldest.Items |> List.map (fun item -> caseAlias database.Fixture item.CaseId))
            [ "synthetic-b" ]
            "A financial OPEN accepts the minimum Gregorian notification date and indexes it exactly"

        let! oldestDetail =
            Service.get
                actor
                {
                    Target = CaseSelector.Id oldest.Items.Head.CaseId
                }
                token

        let firstDate =
            (read oldestDetail).Snapshot.State
            |> CaseState.fields
            |> Option.bind (fun fields -> Registration.receivedDate fields.Registration)
            |> Option.map CalendarDate.format

        Expect.equal
            firstDate
            (Some "0001-01-01")
            "The ordered case retains the exact finite minimum date"

        Expect.isSome
            oldest.NextCursor
            "The minimum-date row has a later notification-date continuation"

        let! later =
            Service.list
                actor
                {
                    Page =
                        {
                            AfterCursor = oldest.NextCursor
                            Limit = 1
                        }
                    Filter = notified
                }
                token

        Expect.equal
            ((read later).Items
             |> List.map (fun item -> caseAlias database.Fixture item.CaseId))
            [ "synthetic-a" ]
            "A notification cursor does not turn the finite minimum into provider negative infinity and repeat the first row"

        let! _ = verified database
        return ()
    }

let private atomicRollback database =
    task {
        let! actor = populated database

        let! initial =
            Service.executeSubmission
                actor
                (reminder
                    (database.Fixture)
                    (200)
                    "reminder.create"
                    (None)
                    (ClaimCore.TestSupport.Submissions.reminderPlan
                        ("Original action")
                        ("2027-01-01")))
                token

        accepted initial |> ignore
        let! before = verified database

        do!
            sql
                database
                (sprintf
                    "CREATE FUNCTION claimcore.inject_reminder_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.reminder_id='%O'::uuid THEN RAISE EXCEPTION 'intended synthetic second-effect failure'; END IF; RETURN NEW; END $$; CREATE TRIGGER inject_reminder_failure BEFORE INSERT ON claimcore.reminders FOR EACH ROW EXECUTE FUNCTION claimcore.inject_reminder_failure()"
                    (id database.Fixture 201))
                [||]

        let operation =
            reminder
                (database.Fixture)
                (201)
                "reminder.completeAndPlanNext"
                (Some((id database.Fixture 200), (1L)))
                (ClaimCore.TestSupport.Submissions.reminderPlan
                    ("Replacement action")
                    ("2027-01-02"))

        let! failed = Service.executeSubmission actor operation token

        match failed.Outcome with
        | ExecuteResult.Failed _ -> ()
        | other -> failtestf "The actual second projection must fail: %A" other

        let! after = verified database

        Expect.equal
            after
            before
            "Actual PostgreSQL rolls back both ledger/head and the earlier completed projection"

        let! page = queue actor None
        let remaining = (read page).Items |> List.exactlyOne |> _.Reminder

        Expect.equal
            (Reminder.id remaining, Reminder.revision remaining, Reminder.state remaining)
            (ReminderId.ofOperationId (CaseFixtures.operationId (id database.Fixture 200)),
             Revision.first,
             ReminderState.Pending)
            "The original is still pending at revision one"

        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows
                connection
                "SELECT count(*) FROM claimcore.ledger WHERE operation_id=$1 UNION ALL SELECT count(*) FROM claimcore.reminders WHERE reminder_id=$1"
                [| box (id database.Fixture 201) |]
                token

        Expect.equal
            (rows |> List.map (DatabaseSession.value<int64> 0))
            [ 0L; 0L ]
            "No partial accepted operation or reserved replacement was persisted"

        do!
            sql
                database
                "DROP TRIGGER inject_reminder_failure ON claimcore.reminders; DROP FUNCTION claimcore.inject_reminder_failure()"
                [||]

        let! acceptedPair = Service.executeSubmission actor operation token
        let receipt = accepted acceptedPair

        match receipt.Subject with
        | ReceiptSubject.Reminder {
                                      Effects = ReminderReceiptEffects.CompletedAndPlanned(completed,
                                                                                           next)
                                  } ->
            Expect.equal
                (completed.Revision, next.ReminderId, next.Revision)
                (CaseFixtures.revision 2L,
                 ReminderId.ofOperationId (CaseFixtures.operationId (id database.Fixture 201)),
                 Revision.first)
                "Positive control accepts one fixed correlated pair"
        | other -> failtestf "Expected compound receipt: %A" other

        let! replay = Service.executeSubmission actor operation token

        match replay.Outcome with
        | ExecuteResult.AlreadyAccepted replay ->
            expectReceipt replay receipt "Accepted retry preserves the original pair"
        | other -> failtestf "Expected replay: %A" other

        let! _ = verified database
        return ()
    }

let private causalEffect database =
    task {
        let! actor = populated database

        let! planned =
            Service.executeSubmission
                actor
                (reminder
                    (database.Fixture)
                    (300)
                    "reminder.create"
                    (None)
                    (ClaimCore.TestSupport.Submissions.reminderPlan
                        ("Original action")
                        ("2027-01-01")))
                token

        accepted planned |> ignore

        let! completed =
            Service.executeSubmission
                actor
                (reminder
                    (database.Fixture)
                    (301)
                    "reminder.complete"
                    (Some((id database.Fixture 300), (1L)))
                    (Map.empty))
                token

        let receipt = accepted completed
        let! _ = verified database

        do!
            rewrite database receipt.Position.Sequence (fun original ->
                let change = original.Changes |> List.exactlyOne

                let changed =
                    match change.Value with
                    | PartValue.Reminder(ReminderValue.Available(plan, handling)) ->
                        let forged =
                            { plan with
                                Due = DomainInputs.date "2027-01-02"
                            }

                        { change with
                            Value = PartValue.Reminder(ReminderValue.Available(forged, handling))
                        }
                    | _ -> failtest "A single completion part is required"

                let nonce =
                    original.Root.Structure.Compartments
                    |> List.exactlyOne
                    |> Compartment.content
                    |> Option.get
                    |> fst

                recreate
                    original.Root.Header
                    original.Root.Structure.Task
                    original.Root.Structure.Binding
                    original.Inputs.Value
                    [ changed ]
                    nonce)


        do! finding VerificationFinding.StateSequence database
    }

let private causalParent database =
    task {
        let! actor = populated database

        let! other =
            Service.executeSubmission
                actor
                (((request
                    (database.Fixture)
                    (400)
                    ("synthetic-b")
                    (0L)
                    "case.open"
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values (registration)
                    )))))
                token

        let other = accepted other |> caseReceipt

        let! planned =
            Service.executeSubmission
                actor
                (reminder
                    (database.Fixture)
                    (401)
                    "reminder.create"
                    (None)
                    (ClaimCore.TestSupport.Submissions.reminderPlan
                        ("Original action")
                        ("2027-01-01")))
                token

        accepted planned |> ignore
        let! _ = verified database

        let receipt = accepted planned

        do!
            rewrite database receipt.Position.Sequence (fun original ->
                let binding =
                    match original.Root.Structure.Binding with
                    | Binding.ReminderNew _ -> Binding.ReminderNew(CaseSelector.Id other.CaseId)
                    | _ -> failtest "Reminder creation binding required"

                rehashed
                    original.Root.Header
                    original.Root.Structure.Task
                    binding
                    original.Root.Structure.Retained
                    original.Root.Structure.Effects
                    original.Root.Structure.Compartments)

        do! finding VerificationFinding.StateSequence database

    }

let private causalRevision database =
    task {
        let! actor = populated database

        let! planned =
            Service.executeSubmission
                actor
                (reminder
                    (database.Fixture)
                    (700)
                    "reminder.create"
                    (None)
                    (ClaimCore.TestSupport.Submissions.reminderPlan
                        ("Original action")
                        ("2027-01-01")))
                token

        accepted planned |> ignore

        let! completed =
            Service.executeSubmission
                actor
                (reminder
                    (database.Fixture)
                    (701)
                    "reminder.complete"
                    (Some((id database.Fixture 700), (1L)))
                    (Map.empty))
                token

        accepted completed |> ignore
        let! _ = verified database

        let receipt = accepted completed

        do!
            rewrite database receipt.Position.Sequence (fun original ->
                let binding =
                    match original.Root.Structure.Binding with
                    | Binding.Reminder(target, reminder, _) ->
                        Binding.Reminder(
                            target,
                            reminder,
                            CaseFixtures.revision SafeInteger.Maximum
                        )
                    | _ -> failtest "Existing reminder binding required"

                rehashed
                    original.Root.Header
                    original.Root.Structure.Task
                    binding
                    original.Root.Structure.Retained
                    original.Root.Structure.Effects
                    original.Root.Structure.Compartments)

        do! finding VerificationFinding.StateSequence database

    }

let private projectionRepair database =
    task {
        let! actor = populated database

        let! planned =
            Service.executeSubmission
                actor
                (reminder
                    (database.Fixture)
                    (500)
                    "reminder.create"
                    (None)
                    (ClaimCore.TestSupport.Submissions.reminderPlan
                        ("Original action")
                        ("2027-01-01")))
                token

        accepted planned |> ignore
        let! checkpoint = checkpoint database
        do! sql database "UPDATE claimcore.reminders SET due=DATE '2027-01-02'" [||]
        do! finding VerificationFinding.CaseWorkProjection database
        do! enterMaintenance database

        let! repaired =
            ProjectionRebuild.rebuild
                database.OwnerSource
                database.MasterKey
                checkpoint
                (MaintenanceHandle.create
                    1L
                    (System.Guid.Parse "10000000-0000-4000-8000-000000000099")
                 |> Option.get)
                token

        Expect.isOk repaired "Rebuild reconstructs the original date from verified causal history"
        let! _ = verified database

        do! sql database "UPDATE claimcore.cases SET follow_up=DATE '2027-01-02'" [||]
        let! inconsistent = Service.get actor { Target = target } token

        Expect.equal
            inconsistent
            (Outcome.Failed CoreFault.StoreIntegrityError)
            "A point read checks derived scheduling against the independent reminder database.Fixture owner"

        do! finding VerificationFinding.CaseWorkProjection database

        let! repairedDate =
            ProjectionRebuild.rebuild
                database.OwnerSource
                database.MasterKey
                checkpoint
                (MaintenanceHandle.create
                    1L
                    (System.Guid.Parse "10000000-0000-4000-8000-000000000099")
                 |> Option.get)
                token

        Expect.isOk repairedDate "Rebuild reconciles the denormalized next-date projection"

        do!
            sql
                database
                "UPDATE claimcore.cases SET work_search_text='unrelated search payload'"
                [||]

        let! inconsistentSearch = Service.get actor { Target = target } token

        Expect.equal
            inconsistentSearch
            (Outcome.Failed CoreFault.StoreIntegrityError)
            "A point read checks the work search projection against the summary owner"

        do! finding VerificationFinding.CaseWorkProjection database

        let! repairedSearch =
            ProjectionRebuild.rebuild
                database.OwnerSource
                database.MasterKey
                checkpoint
                (MaintenanceHandle.create
                    1L
                    (System.Guid.Parse "10000000-0000-4000-8000-000000000099")
                 |> Option.get)
                token

        Expect.isOk repairedSearch "Independent work search columns are also part of reconstruction"
        let! _ = verified database

        for table in [ "case_work"; "reminders" ] do
            do!
                sql
                    database
                    ("UPDATE claimcore."
                     + table
                     + " SET snapshot=decode('20','hex')||snapshot WHERE snapshot IS NOT NULL")
                    [||]

            do! finding VerificationFinding.CaseWorkProjection database

            let! repairedBytes =
                ProjectionRebuild.rebuild
                    database.OwnerSource
                    database.MasterKey
                    checkpoint
                    (MaintenanceHandle.create
                        1L
                        (System.Guid.Parse "10000000-0000-4000-8000-000000000099")
                     |> Option.get)
                    token

            Expect.isOk
                repairedBytes
                "Rebuild restores canonical subject bytes instead of accepting a re-encoded projection"

            let! _ = verified database
            ()

        return ()
    }

let private casePageOwners database =
    task {
        let! actor = populated database

        let! updated =
            Service.executeSubmission
                actor
                ((ClaimCore.TestSupport.Submissions.createRaw)
                    "work.update"
                    (id database.Fixture 550)
                    (ClaimCore.Application.Contracts.OperationBinding.Work((target), (0L)))
                    (ClaimCore.TestSupport.Submissions.work (
                        {
                            Summary = WorkValue.Set "Page work summary"
                            Responsible = WorkValue.Keep
                        }
                    )))
                token

        accepted updated |> ignore

        let! planned =
            Service.executeSubmission
                actor
                (reminder
                    (database.Fixture)
                    (551)
                    "reminder.create"
                    (None)
                    (ClaimCore.TestSupport.Submissions.reminderPlan
                        ("Current page action")
                        ("2027-01-01")))
                token

        accepted planned |> ignore

        let request =
            {
                CaseListRequest.Page = { AfterCursor = None; Limit = 1 }
                Filter = CaseListFilter.empty
            }

        let check label =
            task {
                let! result = Service.list actor request token
                Expect.equal result (Outcome.Failed CoreFault.StoreIntegrityError) label
            }

        let! positive = Service.list actor request token

        let item = (read positive).Items |> List.exactlyOne
        let! detail = Service.get actor { Target = CaseSelector.Id item.CaseId } token

        Expect.equal
            ((read detail).Work.Summary |> Option.map HandlingSummary.format)
            (Some "Page work summary")
            "The work owner remains available in its detail read"

        Expect.equal
            (item.PendingReminders,
             item.NextAction |> Option.map (fun next -> ReminderAction.format next.Action))
            (1L, Some "Current page action")
            "The page reads the independent reminder database.Fixture owner"

        let! checkpoint = checkpoint database
        do! sql database "UPDATE claimcore.case_work SET search_text='wrong work owner'" [||]
        do! check "The joined work columns are checked against their own snapshot"

        do!
            sql
                database
                "UPDATE claimcore.case_work SET search_text='page work summary'; UPDATE claimcore.cases SET work_search_text='wrong case index'"
                [||]

        do!
            check
                "The joined work search cannot collide with the financial row's derived search column"

        do!
            sql
                database
                "UPDATE claimcore.cases SET work_search_text='page work summary'; DELETE FROM claimcore.case_work"
                [||]

        do! check "A missing work root refuses instead of dropping the selected parent"
        do! enterMaintenance database

        let! repaired =
            ProjectionRebuild.rebuild
                database.OwnerSource
                database.MasterKey
                checkpoint
                (MaintenanceHandle.create
                    1L
                    (System.Guid.Parse "10000000-0000-4000-8000-000000000099")
                 |> Option.get)
                token

        Expect.isOk repaired "Verified reconstruction restores the missing work owner"
        let! after = Service.list actor request token

        let restored = (read after).Items |> List.exactlyOne

        Expect.equal
            (restored.NextAction
             |> Option.map (fun next -> ReminderAction.format next.Action))
            (Some "Current page action")
            "The replacement page path reads the repaired independent work root"

        let! _ = verified database
        return ()
    }

let private currentLookup database =
    task {
        let! actor = populated database

        for operation, taskName, selected, values in
            [
                600, "reminder.create", None, Submissions.reminderPlan "Earlier action" "2027-01-01"
                601,
                "reminder.update",
                Some(id database.Fixture 600, 1L),
                Submissions.reminderPlan "Current action" "2027-02-01"
                602, "reminder.cancel", Some(id database.Fixture 600, 2L), Map.empty
            ] do
            let! result =
                Service.executeSubmission
                    actor
                    (reminder database.Fixture operation taskName selected values)
                    token

            accepted result |> ignore

        let reader =
            PrincipalKey.human "https://issuer.example/" "reminder-current-reader" |> parsed

        let! registered =
            (task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (database.Fixture.Store)
                        ((id database.Fixture 603))
                        (reader)
                        (None)
                        "people.register"
                        (ClaimCore.TestSupport.Submissions.profile (
                            ClaimCore.TestSupport.DomainInputs.actorProfile
                        ))

                return! Service.executeSubmission (actor) submission (token)
            })

        acceptedPeople registered

        let! granted =
            grant
                actor
                (id database.Fixture 604)
                reader
                {
                    Role = Role.CaseReader
                    Scope = caseGrant database.Fixture "synthetic-a"
                }
                true

        acceptedPeople granted
        let services = core database.Fixture reader

        let! result =
            Service.reminder
                services
                {
                    Target = target
                    ReminderId = id database.Fixture 600
                }
                token

        let current = read result

        Expect.equal
            (Reminder.state current.Item.Reminder,
             ReminderAction.format (Reminder.action current.Item.Reminder),
             CalendarDate.format (Reminder.due current.Item.Reminder))
            (ReminderState.Cancelled, "Current action", "2027-02-01")
            "ReadCase discloses the current terminal record, not earlier text or dates"

        Expect.isEmpty
            current.Item.Acts
            "Current read access does not create work-management people"

        Expect.equal
            current.Basis.TimeZoneId
            database.Fixture.Calendar.TimeZoneId
            "Singular reads carry the actual installation basis"

        let! history =
            Service.timeline
                services
                {
                    Target = target
                    AfterCursor = None
                    Limit = 10
                    Filter = Some TimelineFilter.Work
                }
                token

        let historical = read history

        Expect.isNonEmpty
            historical.Items
            "Basic history remains available under current read rights."

        for entry in historical.Items do
            Expect.equal
                entry.Changes
                TimelineChanges.Withheld
                "ReadCase never discloses earlier reminder payloads."

        let! other =
            Service.executeSubmission
                actor
                (((request
                    (database.Fixture)
                    (605)
                    ("synthetic-b")
                    (0L)
                    "case.open"
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values (registration)
                    )))))
                token

        accepted other |> ignore

        let! planned =
            Service.executeSubmission
                actor
                ((ClaimCore.TestSupport.Submissions.createRaw)
                    "reminder.create"
                    (id database.Fixture 606)
                    (ClaimCore.Application.Contracts.OperationBinding.Reminder(
                        (caseTarget database.Fixture "synthetic-b"),
                        None
                    ))
                    (ClaimCore.TestSupport.Submissions.reminderPlan
                        ("Other case action")
                        ("2027-01-01")))
                token

        accepted planned |> ignore

        for requested in [ id database.Fixture 606; id database.Fixture 999 ] do
            let! hidden =
                Service.reminder
                    services
                    {
                        Target = target
                        ReminderId = requested
                    }
                    token

            Expect.equal
                hidden
                (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
                "A foreign identity and an unknown identity have the same private result"

        let! malformed =
            Service.reminder
                services
                {
                    Target = target
                    ReminderId = Guid.Empty
                }
                token

        match malformed with
        | Outcome.Rejected(Refusal.InvalidInput issues) ->
            Expect.equal
                ((InputIssues.head issues).Target, (InputIssues.head issues).Violation)
                (InputTarget.ReminderId, Violation.ReminderIdRequired)
                "Malformed identity has a reminder-specific input diagnostic"
        | other -> failtestf "Expected malformed reminder database.Fixture identity input: %A" other
    }

[<Tests>]
let tests =
    testList
        "PostgreSQL reminder database.Fixture identity and effects"
        [
            testCaseAsync
                "case page joins preserve independent owner and missing-root integrity failures"
                (run casePageOwners)
            testCaseAsync
                "scoped current lookup includes terminal records without historical disclosure"
                (run currentLookup)
            testCaseAsync "kind filters reach the private PostgreSQL projection" (run deadlineKinds)
            testCaseAsync
                "Gregorian extremes survive current rows and both queue and case continuations"
                (run dates)
            testCaseAsync
                "injected second-effect failure rolls back the entire compound operation"
                (run atomicRollback)
            testCaseAsync
                "rehashed completion cannot silently change the retained due date"
                (run causalEffect)
            testCaseAsync
                "reminder public parent must match its retained closure"
                (run causalParent)
            testCaseAsync
                "maximum retained expected revision returns a finding without overflow"
                (run causalRevision)
            testCaseAsync
                "verification and rebuild cover reminder database.Fixture dates and independent work search"
                (run projectionRepair)
        ]
