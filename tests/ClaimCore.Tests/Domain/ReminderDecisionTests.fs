module ClaimCore.Tests.ReminderDecisionTests

open System
open Expecto
open ClaimCore.Domain

let private required =
    function
    | Ok value -> value
    | Error issue -> failtestf "%A" issue

let private operation number =
    Guid.Parse(sprintf "98000000-0000-7000-8000-%012d" number)
    |> OperationId.create
    |> Option.get

let private date value = CalendarDate.parse value |> required

let private plan action due : ReminderPlan =
    {
        Action = ReminderAction.parse action |> required
        Due = date due
    }

let private first = plan "Request incident photographs" "2026-11-01"
let private next = plan "Review received photographs" "2026-11-10"

let private parent () =
    Case.parse
        {
            OperationId = OperationId.value (operation 1)
            CaseReference = "REMINDER-QA"
            ExpectedRevision = 0L
            Act =
                CaseAct.Open(
                    RegistrationInput.create
                        [
                            BusinessField.IncidentDate, "2026-01-01"
                            BusinessField.IncidentNotificationDate, "2026-01-02"
                            BusinessField.IncidentCountry, "LV"
                            BusinessField.ClaimantName, "Photographs claimant"
                            BusinessField.InsurerName, "Photographs insurer"
                            BusinessField.ClaimedAmount, "500.00"
                            BusinessField.ClaimedCurrency, "EUR"
                        ]
                    |> required
                )
        }
    |> Result.mapError CaseDecisionError.Refused
    |> Result.bind (fun command ->
        Case.decide (DateOnly(2026, 11, 2)) CasePaymentContext.NoneRequired command None)
    |> required
    |> _.After

let private created parent number =
    match
        Reminder.decide
            (operation number)
            parent
            None
            false
            (ReminderCommand.Create(parent.CaseId, first))
        |> required
    with
    | ReminderEffects.Created value -> value
    | other -> failtestf "Expected one creation, got %A" other

let private change operation parent reminder expected act =
    Reminder.decide
        operation
        parent
        (Some reminder)
        false
        (ReminderCommand.Change(parent.CaseId, Reminder.id reminder, expected, act))

let private changed =
    function
    | ReminderEffects.Changed value -> value
    | other -> failtestf "Expected exactly one change, got %A" other

[<Tests>]
let tests =
    testList
        "reminder decisions"
        [
            testCase
                "current reminder timing uses its supplied business date and retained state"
                (fun () ->
                    let parent = parent ()
                    let pending = created parent 2

                    for today, expected in
                        [
                            DateOnly(2026, 10, 31), ReminderDue.Upcoming
                            DateOnly(2026, 11, 1), ReminderDue.Today
                            DateOnly(2026, 11, 2), ReminderDue.Overdue
                        ] do
                        Expect.equal
                            (ReminderDue.at today true pending)
                            (Some expected)
                            "The read's date owns the classification"

                        Expect.equal
                            (Reminder.state pending)
                            ReminderState.Pending
                            "Elapsed dates never complete outstanding work"

                        Expect.equal
                            (ReminderDue.at today false pending)
                            None
                            "Inactive parents have no actionable timing"

                    for state in [ ReminderState.Completed; ReminderState.Cancelled ] do
                        let terminal =
                            Reminder.ofAccepted
                                (Reminder.id pending)
                                parent.CaseId
                                Revision.first
                                (Reminder.action pending)
                                (Reminder.due pending)
                                state
                            |> required

                        Expect.equal
                            (ReminderDue.at (DateOnly(2031, 1, 1)) true terminal)
                            None
                            "Terminal reminders are not actionable overdue work")
            testCase "actionable parent policy excludes closed voided and erased work" (fun () ->
                for lifecycle, status, expected in
                    [
                        Lifecycle.Active, Some CaseStatus.Opened, true
                        Lifecycle.Active, Some CaseStatus.Closed, false
                        Lifecycle.Voided, Some CaseStatus.Opened, false
                        Lifecycle.Voided, Some CaseStatus.Closed, false
                        Lifecycle.Erased, None, false
                    ] do
                    Expect.equal
                        (CaseState.isActionable lifecycle status)
                        expected
                        "Parent lifecycle and status are evaluated together")
            testCase
                "creation derives identity and owns revision without changing financial parent"
                (fun () ->
                    let parent = parent ()
                    let reminder = created parent 2

                    Expect.equal
                        (Reminder.id reminder |> ReminderId.format)
                        "98000000-0000-7000-8000-000000000002"
                        "Creation operation identity"

                    Expect.equal (Reminder.caseId reminder) parent.CaseId "Immutable parent"

                    Expect.equal
                        (Reminder.revision reminder |> Revision.value)
                        1L
                        "Independent first version"

                    Expect.equal
                        (Reminder.state reminder)
                        ReminderState.Pending
                        "Past due remains pending"

                    Expect.equal
                        (CalendarDate.format (Reminder.due reminder))
                        "2026-11-01"
                        "Retained due"

                    Expect.equal (Revision.value parent.Revision) 1L "No parent mutation")
            testCase
                "different reminders accept their own revisions and same-reminder stale edits conflict"
                (fun () ->
                    let parent = parent ()
                    let a, b = created parent 2, created parent 3

                    let updatedA =
                        change (operation 4) parent a Revision.first (ReminderChange.Update next)
                        |> required
                        |> changed

                    let updatedB =
                        change (operation 5) parent b Revision.first (ReminderChange.Update next)
                        |> required
                        |> changed

                    Expect.equal
                        (Revision.value (Reminder.revision updatedA))
                        2L
                        "First independent edit"

                    Expect.equal
                        (Revision.value (Reminder.revision updatedB))
                        2L
                        "Second independent edit"

                    Expect.equal
                        (change
                            (operation 6)
                            parent
                            updatedA
                            Revision.first
                            ReminderChange.Complete)
                        (Error(ReminderRefusal.RevisionConflict(Revision.create 2L |> Option.get)))
                        "No silent rebasing of a stale same-entity edit")
            testCase
                "compound result is exactly completion plus new operation-derived reminder"
                (fun () ->
                    let parent = parent ()
                    let before = created parent 2

                    match
                        change
                            (operation 3)
                            parent
                            before
                            Revision.first
                            (ReminderChange.CompleteAndPlanNext next)
                        |> required
                    with
                    | ReminderEffects.CompletedAndPlanned(doneReminder, replacement) ->
                        Expect.equal
                            (Reminder.id doneReminder)
                            (Reminder.id before)
                            "Same completed entity"

                        Expect.equal
                            (Reminder.state doneReminder)
                            ReminderState.Completed
                            "Completed first effect"

                        Expect.equal
                            (Revision.value (Reminder.revision doneReminder))
                            2L
                            "Completed revision"

                        Expect.equal
                            (Reminder.id replacement |> ReminderId.format)
                            "98000000-0000-7000-8000-000000000003"
                            "New identity is compound operation"

                        Expect.equal
                            (Reminder.state replacement)
                            ReminderState.Pending
                            "Pending second effect"

                        Expect.equal
                            (Revision.value (Reminder.revision replacement))
                            1L
                            "Replacement revision one"

                        Expect.equal (Reminder.caseId replacement) parent.CaseId "Same parent"

                        Expect.equal
                            (ReminderAction.format (Reminder.action replacement))
                            "Review received photographs"
                            "Replacement text"
                    | other -> failtestf "Missing bounded pair: %A" other)
            testCase
                "invalid compound replacement returns no effects and preserves original"
                (fun () ->
                    let parent = parent ()
                    let before = created parent 2
                    let invalid = plan "   " "2026-11-10"

                    Expect.equal
                        (change
                            (operation 3)
                            parent
                            before
                            Revision.first
                            (ReminderChange.CompleteAndPlanNext invalid))
                        (Error(ReminderRefusal.InvalidAction Violation.TextRequired))
                        "No half result"

                    Expect.equal (Reminder.state before) ReminderState.Pending "Original unchanged"
                    Expect.equal (Revision.value (Reminder.revision before)) 1L "No new revision")
            testCase
                "closed active parent permits cleanup but rejects planning and compound"
                (fun () ->
                    let opened = parent ()
                    let reminder = created opened 2
                    let fields = CaseState.fields opened.State |> Option.get

                    let closed =
                        { opened with
                            State =
                                CaseState.Active
                                    { fields with
                                        Status = CaseStatus.Closed
                                    }
                        }

                    for act in [ ReminderChange.Complete; ReminderChange.Cancel ] do
                        Expect.isOk
                            (change (operation 3) closed reminder Revision.first act)
                            "Cleanup accepted"

                    for act in
                        [
                            ReminderChange.Update next
                            ReminderChange.Reopen(date "2026-11-10")
                            ReminderChange.CompleteAndPlanNext next
                        ] do
                        Expect.equal
                            (change (operation 3) closed reminder Revision.first act)
                            (Error ReminderRefusal.ParentNotOpen)
                            "No planning after closure"

                    Expect.equal
                        (Reminder.decide
                            (operation 3)
                            closed
                            None
                            false
                            (ReminderCommand.Create(closed.CaseId, next)))
                        (Error ReminderRefusal.ParentNotOpen)
                        "Creation also refuses")
            testCase
                "terminal records must reopen explicitly and preserve identity and content"
                (fun () ->
                    let parent = parent ()
                    let before = created parent 2

                    for terminal in [ ReminderChange.Complete; ReminderChange.Cancel ] do
                        let ended =
                            change (operation 3) parent before Revision.first terminal
                            |> required
                            |> changed

                        for act in
                            [
                                ReminderChange.Complete
                                ReminderChange.Cancel
                                ReminderChange.Update next
                                ReminderChange.CompleteAndPlanNext next
                            ] do
                            Expect.equal
                                (change (operation 4) parent ended (Reminder.revision ended) act)
                                (Error ReminderRefusal.PendingRequired)
                                "No terminal rewrite or no-op"

                        let reopened =
                            change
                                (operation 5)
                                parent
                                ended
                                (Reminder.revision ended)
                                (ReminderChange.Reopen(Reminder.due ended))
                            |> required
                            |> changed

                        Expect.equal (Reminder.id reopened) (Reminder.id before) "No new identity"

                        Expect.equal
                            (Reminder.action reopened)
                            (Reminder.action before)
                            "Action preserved"

                        Expect.equal
                            (Reminder.due reopened)
                            (Reminder.due before)
                            "Explicit same date is valid"

                        Expect.equal
                            (Reminder.state reopened)
                            ReminderState.Pending
                            "Pending again"

                        Expect.equal
                            (Revision.value (Reminder.revision reopened))
                            3L
                            "New version keeps history separate")
            testCase "no-op edits and reopening pending refuse" (fun () ->
                let parent = parent ()
                let before = created parent 2

                Expect.equal
                    (change
                        (operation 3)
                        parent
                        before
                        Revision.first
                        (ReminderChange.Update first))
                    (Error ReminderRefusal.NoChanges)
                    "No empty update event"

                Expect.equal
                    (change
                        (operation 3)
                        parent
                        before
                        Revision.first
                        (ReminderChange.Reopen first.Due))
                    (Error ReminderRefusal.TerminalRequired)
                    "Not already pending")
            testCase
                "voided erased foreign-parent and reserved identities cannot yield effects"
                (fun () ->
                    let parent = parent ()
                    let before = created parent 2
                    let fields = CaseState.fields parent.State |> Option.get

                    for state in [ CaseState.Voided fields; CaseState.Erased ] do
                        let blocked = { parent with State = state }

                        Expect.equal
                            (change
                                (operation 3)
                                blocked
                                before
                                Revision.first
                                ReminderChange.Complete)
                            (Error ReminderRefusal.ParentNotActive)
                            "No inactive mutation"

                    let foreign = CaseId.ofOperationId (operation 9)

                    Expect.equal
                        (Reminder.decide
                            (operation 3)
                            parent
                            (Some before)
                            false
                            (ReminderCommand.Change(
                                foreign,
                                Reminder.id before,
                                Revision.first,
                                ReminderChange.Complete
                            )))
                        (Error ReminderRefusal.ParentMismatch)
                        "Explicit parent correlation"

                    Expect.equal
                        (Reminder.decide
                            (operation 3)
                            parent
                            None
                            true
                            (ReminderCommand.Create(parent.CaseId, next)))
                        (Error ReminderRefusal.IdentityReserved)
                        "Tombstones reserve IDs"

                    Expect.equal
                        (Reminder.decide
                            (operation 3)
                            parent
                            (Some before)
                            true
                            (ReminderCommand.Change(
                                parent.CaseId,
                                Reminder.id before,
                                Revision.first,
                                ReminderChange.CompleteAndPlanNext next
                            )))
                        (Error ReminderRefusal.IdentityReserved)
                        "Entire compound refuses"

                    Expect.equal
                        (change
                            (operation 2)
                            parent
                            before
                            Revision.first
                            (ReminderChange.CompleteAndPlanNext next))
                        (Error ReminderRefusal.IdentityReserved)
                        "Creation ID cannot replace original entity")
            testCase
                "rescheduling preserves retained action meaning while new assertions use current admission"
                (fun () ->
                    let parent = parent ()
                    let oldText = String.replicate 201 "x"
                    let retainedAction = ReminderAction.parse oldText |> required

                    let retained =
                        Reminder.ofAccepted
                            (ReminderId.ofOperationId (operation 2))
                            parent.CaseId
                            Revision.first
                            retainedAction
                            first.Due
                            ReminderState.Pending
                        |> required

                    let rescheduled =
                        change
                            (operation 3)
                            parent
                            retained
                            Revision.first
                            (ReminderChange.Update
                                {
                                    Action = retainedAction
                                    Due = next.Due
                                })
                        |> required
                        |> changed

                    Expect.equal
                        (Reminder.action rescheduled)
                        retainedAction
                        "Omitted/unchanged accepted meaning is retained"

                    Expect.equal (Reminder.due rescheduled) next.Due "Actual planned-date edit"

                    let replacement: ReminderPlan =
                        {
                            Action = ReminderAction.parse (String.replicate 201 "y") |> required
                            Due = next.Due
                        }

                    Expect.equal
                        (change
                            (operation 4)
                            parent
                            retained
                            Revision.first
                            (ReminderChange.Update replacement))
                        (Error(ReminderRefusal.InvalidAction(Violation.TooLong 200)))
                        "Different action is a new assertion"

                    Expect.equal
                        (change
                            (operation 5)
                            parent
                            retained
                            Revision.first
                            (ReminderChange.CompleteAndPlanNext
                                {
                                    Action = retainedAction
                                    Due = next.Due
                                }))
                        (Error(ReminderRefusal.InvalidAction(Violation.TooLong 200)))
                        "New entity cannot grandfather its replacement assertion")
        ]
