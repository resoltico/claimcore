module ClaimCore.Tests.ReminderReplyTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Cli

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
        RecordedBy = ActorId.create identity |> Option.get
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
        TextWorkReply.single language { Basis = dateBasis; Item = value }
        TextWorkReply.page
            language
            {
                Basis = dateBasis
                Items = [ value ]
                NextCursor = None
            }
    ]
    |> List.map (String.concat "\n")

[<Tests>]
let tests =
    testList
        "current reminder human replies"
        [
            for language, inactive, labels in
                [
                    "en", "Inactive work", [ "Overdue"; "Today"; "Upcoming" ]
                    "lv", "Neaktīvs darbs", [ "Nokavēts"; "Šodien"; "Gaidāms" ]
                    "ar", "عمل غير نشط", [ "متأخر"; "اليوم"; "قادم" ]
                ] do
                testCase
                    (language + " single and page preserve server timing and inactive work")
                    (fun () ->
                        for timing, expected in
                            List.zip
                                [ ReminderDue.Overdue; ReminderDue.Today; ReminderDue.Upcoming ]
                                labels do
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
                                    (businessDate.ToString("yyyy-MM-dd") + "  Europe/Riga")
                                    "Installation business-date basis"

                                Expect.stringContains
                                    text
                                    "2026-11-01"
                                    "Stored due date is preserved"

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
                        let text =
                            TextWorkReply.reminder language (reminder ReminderState.Pending)
                            |> String.concat "\n"

                        for label in inactive :: labels do
                            Expect.isFalse
                                (text.Contains label)
                                "Receipts and activity snapshots have no current observation")
        ]
