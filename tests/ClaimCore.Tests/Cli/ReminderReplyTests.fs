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
    Reminder.ofAccepted
        (ReminderId.create identity |> Option.get)
        (CaseId.create identity |> Option.get)
        Revision.first
        (ReminderAction.parse "Request photographs" |> required)
        (CalendarDate.parse "2026-11-01" |> required)
        state
    |> required

let private item active state timing : ReminderReadModel =
    {
        Reminder = reminder state
        Reference = CaseReference.parse "CLI-TIMING" |> required
        RecordedAt = DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)
        RecordedBy =
            ActorId.create identity
            |> Option.get
            |> ClaimCore.TestSupport.Inputs.actorReference
        Acts = []
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
        ViewText.render language (Descriptor.reminder value |> Descriptor.recordView)
        ViewText.render language (Descriptor.tableView Descriptor.reminderColumns [ value ])
    ]
    |> List.map (fun lines ->
        lines @ [ TextObservation.line language dateBasis ] |> String.concat "\n")

[<Tests>]
let tests =
    testList
        "current reminder human replies"
        [
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
                                    language
                                    observed
                                    (item true ReminderState.Pending (Some timing)) do
                                Expect.stringContains text expected "Exact localized server timing"

                                Expect.stringContains
                                    text
                                    (expectedDate + " (Europe/Riga)")
                                    "Installation business-date basis"

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
                                    let outputs = render language basis (item active state None)

                                    for text in outputs do
                                        Expect.equal
                                            (text.Contains inactive)
                                            (not active)
                                            "Inactive indication follows actual parent activity"

                                        for label in labels do
                                            Expect.isFalse
                                                (text.Contains label)
                                                "No timing is invented for absent server timing"

                        let active = render language basis (item true ReminderState.Completed None)

                        let dormant =
                            render language basis (item false ReminderState.Completed None)

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
                                                CaseReference.parse "CLI-TIMING" |> required
                                            Act = ReminderActKind.Create
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
                                language
                                (Descriptor.receipt receipt |> Descriptor.recordView)
                            |> String.concat "\n"

                        for label in inactive :: labels do
                            Expect.isFalse
                                (text.Contains label)
                                "Receipts and activity snapshots have no current observation")
        ]
