module ClaimCore.Tests.ReminderReplyTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Cli
open ClaimCore.Presentation

let private required result =
    result |> Result.defaultWith (fun issue -> failtestf "%A" issue)

let private identity = Guid.Parse "98000000-0000-4000-8000-000000000001"

let private reminder state =
    Reminder.decode
        (ReminderId.create identity |> Option.get)
        (CaseId.create identity |> Option.get)
        Revision.first
        (ReminderAction.parse "Request photographs" |> required)
        (CalendarDate.parse "2026-11-01" |> required)
        ReminderKind.FollowUp
        state
        (if state = ReminderState.Completed then
             Some(CalendarDate.ofDate (System.DateOnly(2026, 11, 18)))
         else
             None)
    |> required

let private item active state timing : ReminderReadModel =
    {
        Reminder = reminder state
        Reference = ClaimCore.TestSupport.DomainInputs.reference "CC-F17M-B2GM-Q6FJ-1QY0-56"
        Claimants =
            [
                (PartyName.parse "Synthetic claimant" |> Result.defaultWith (string >> failtest))
            ]
        Responsible = None
        RecordedAt = DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)
        RecordedBy =
            ActorId.create identity
            |> Option.get
            |> ClaimCore.TestSupport.DomainInputs.actorReference
        Offers = []
        Active = active
        Timing = timing
    }

let private basis: WorkDateBasis =
    {
        ObservedAt = DateTimeOffset(2026, 11, 3, 1, 0, 0, TimeSpan.Zero)
        BusinessDate = DateOnly(2026, 11, 3)
        TimeZoneId = "Europe/Riga"
    }

let private render language dateBasis value =
    [
        ReminderScreen.document { Basis = dateBasis; Item = value }
        ReminderListScreen.document
            {
                View = None
                Target = None
                State = None
                Kind = None
                DeadlineOutcome = None
                Due = None
                Assignment = None
                Text = None
                AfterCursor = None
                Limit = 50
                IncludeInactive = None
            }
            {
                Basis = dateBasis
                Items = [ value ]
                NextCursor = None
            }
    ]
    |> List.map (fun document ->
        ViewText.renderDocument true language document |> String.concat "\n")


[<Tests>]
let tests =
    testList
        "current reminder human replies"
        [
            testCase
                "reminder due cells retain dates and carry timing only for active pending work"
                (fun () ->
                    let due = CalendarDate.parse "2026-11-01" |> required

                    let column =
                        ReminderListScreen.columnsFor None None
                        |> List.find (fun column -> column.Key = "due")

                    for timing, token in
                        [
                            ReminderDue.Overdue, "OVERDUE"
                            ReminderDue.Today, "TODAY"
                            ReminderDue.Upcoming, "UPCOMING"
                        ] do
                        Expect.equal
                            (column.Read(item true ReminderState.Pending (Some timing)))
                            (PresentedValue.DateWithCode(due, ("reminderDue", token)))
                            "The supplied timing accompanies the due date in the same cell"

                    for active, state in
                        [
                            false, ReminderState.Pending
                            true, ReminderState.Completed
                            true, ReminderState.Cancelled
                        ] do
                        Expect.equal
                            (column.Read(item active state (Some ReminderDue.Overdue)))
                            (PresentedValue.Date due)
                            "Inactive or finished work presents its date without a timing code"

                    Expect.equal
                        (column.Read(item true ReminderState.Pending None))
                        (PresentedValue.Date due)
                        "The presentation does not infer an absent timing code")
            testCase
                "the reminder group carries timing once, and its row preserves the due date"
                (fun () ->
                    let rows = [ item true ReminderState.Pending (Some ReminderDue.Overdue) ]
                    let screen = ReminderListScreen.rows None None rows

                    match screen.Regions.Head.Content with
                    | RegionContent.Table table ->
                        let group = List.exactlyOne table.Groups
                        let row = List.exactlyOne table.Rows

                        Expect.equal
                            group.TitleId
                            "reminderDue.OVERDUE.label"
                            "The group declares timing"

                        Expect.equal
                            row.Values.Head
                            (PresentedValue.Date(Reminder.due rows.Head.Reminder))
                            "The row keeps the date"
                    | _ -> failtest "Expected a grouped reminders table")
            testCase "inactive parent case uses a state label without inventing status" (fun () ->
                let value = item false ReminderState.Pending None
                let entries = ReminderScreen.reminder value |> List.collect _.Entries
                let parent = entries |> List.find (fun entry -> entry.Key = "parentState")
                Expect.equal parent.LabelId "label.caseState" "Inactive work is not a CaseStatus"

                Expect.equal
                    parent.Value
                    (PresentedValue.Code("due", "INACTIVE"))
                    "The supplied coarse parent state remains unchanged"

                let output =
                    ReminderScreen.view { Basis = basis; Item = value }
                    |> ViewText.render (RenderingContext.forLanguage "en")
                    |> String.concat "\n"

                Expect.stringContains
                    output
                    "Case state: Inactive work"
                    "The single reminder names the supplied case state"

                Expect.isFalse
                    (output.Contains "Case status:")
                    "An inactive parent does not invent an operator status")
            for language, inactive, labels, dates, dueDate in
                [
                    "en",
                    "Inactive work",
                    [ "Overdue"; "Today"; "Upcoming" ],
                    [ "3 Nov 2026"; "1 Nov 2026"; "31 Oct 2026" ],
                    "1 Nov 2026"
                    "lv",
                    "Neaktīvs darbs",
                    [ "Termiņš nokavēts"; "Šodien"; "Gaidāms" ],
                    [ "3 nov. 2026"; "1 nov. 2026"; "31 okt. 2026" ],
                    "1 nov. 2026"
                    "ar",
                    "عمل غير نشط",
                    [ "متأخر"; "اليوم"; "قادم" ],
                    [ "3 نوفمبر 2026"; "1 نوفمبر 2026"; "31 أكتوبر 2026" ],
                    "1 نوفمبر 2026"
                ] do
                testCase
                    (language + " single and page preserve server timing and inactive work")
                    (fun () ->
                        for timing, expected, expectedDate in
                            List.zip3
                                [ ReminderDue.Overdue; ReminderDue.Today; ReminderDue.Upcoming ]
                                labels
                                dates do
                            // Coherent server observations remain authoritative even when their date is old.
                            let businessDate =
                                match timing with
                                | ReminderDue.Overdue -> DateOnly(2026, 11, 3)
                                | ReminderDue.Today -> DateOnly(2026, 11, 1)
                                | ReminderDue.Upcoming -> DateOnly(2026, 10, 31)

                            let observed =
                                { basis with
                                    BusinessDate = businessDate
                                    ObservedAt =
                                        DateTimeOffset(
                                            businessDate.ToDateTime(TimeOnly(1, 0)),
                                            TimeSpan.Zero
                                        )
                                }

                            for text in
                                render
                                    (RenderingContext.forLanguage language)
                                    observed
                                    (item true ReminderState.Pending (Some timing)) do
                                Expect.stringContains text expected "Exact localized server timing"

                                Expect.stringContains
                                    text
                                    expectedDate
                                    "Installation business date"

                                Expect.stringContains text "Europe/Riga" "Installation time zone"

                                Expect.stringContains
                                    text
                                    dueDate
                                    "Stored due date is localized without changing its value"

                                Expect.isFalse
                                    (text.Contains inactive)
                                    "Active work has no inactive warning"

                        for state in
                            [
                                ReminderState.Pending
                                ReminderState.Completed
                                ReminderState.Cancelled
                            ] do
                            for active in [ true; false ] do
                                if state <> ReminderState.Pending || not active then
                                    let outputs =
                                        render
                                            (RenderingContext.forLanguage language)
                                            basis
                                            (item active state None)

                                    for text in outputs do
                                        Expect.equal
                                            (text.Contains(
                                                inactive,
                                                StringComparison.OrdinalIgnoreCase
                                            ))
                                            (not active)
                                            "Inactive indication follows actual parent activity"

                                        for label in labels do
                                            Expect.isFalse
                                                (text.Contains label)
                                                "No timing is invented for absent server timing"

                        let active =
                            render
                                (RenderingContext.forLanguage language)
                                basis
                                (item true ReminderState.Completed None)

                        let dormant =
                            render
                                (RenderingContext.forLanguage language)
                                basis
                                (item false ReminderState.Completed None)

                        Expect.notEqual
                            dormant
                            active
                            "Read-only active and inactive records are visibly distinguishable")

                testCase
                    (language + " historical reminder presenter does not invent current timing")
                    (fun () ->
                        let historical = reminder ReminderState.Pending

                        let receipt =
                            { ClaimCore.TestSupport.ResponseExamples.receipt with
                                Subject =
                                    ReceiptSubject.Reminder
                                        {
                                            CaseId =
                                                ClaimCore.TestSupport
                                                    .ResponseExamples.snapshot.CaseId
                                            Reference =
                                                ClaimCore.TestSupport.DomainInputs.reference
                                                    "CC-F17M-B2GM-Q6FJ-1QY0-56"
                                            Act = (TaskId.ofLiteral "reminder.create")
                                            Effects =
                                                ReminderReceiptEffects.Single
                                                    {
                                                        ReminderId = Reminder.id historical
                                                        Revision = Revision.first
                                                        Snapshot =
                                                            ReminderDisclosure.Disclosed historical
                                                    }
                                        }
                            }

                        let text =
                            ViewText.render
                                (RenderingContext.forLanguage language)
                                (ReceiptScreen.view receipt)
                            |> String.concat "\n"

                        for label in inactive :: labels do
                            Expect.isFalse
                                (text.Contains label)
                                "Receipts and activity snapshots have no current observation")
        ]
