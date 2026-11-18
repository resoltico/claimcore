module ClaimCore.Tests.ReminderDueTests

open System
open Expecto
open ClaimCore.Domain

let private required result =
    result |> Result.defaultWith (fun issue -> failtestf "%A" issue)

[<Tests>]
let tests =
    testList
        "reminder timing"
        [
            testCase "deadlines classify due and actual completion dates independently" (fun () ->
                let operation =
                    Guid.Parse "20000000-0000-7000-8000-000000000071"
                    |> OperationId.create
                    |> Option.get

                let plan =
                    {
                        ReminderPlan.Action = ReminderAction.parse "Review evidence" |> required
                        Due = CalendarDate.ofDate (DateOnly(2026, 11, 16))
                        Kind = ReminderKind.Deadline
                    }

                let projected handling =
                    Reminder.ofAccepted
                        (ReminderId.ofOperationId operation)
                        (CaseId.ofOperationId operation)
                        Revision.first
                        plan
                        handling
                    |> required

                for handling, overdue, expected in
                    [
                        ReminderHandling.Pending, true, Some DeadlineOutcome.Overdue
                        ReminderHandling.Pending, false, None
                        ReminderHandling.Completed(CalendarDate.ofDate (DateOnly(2026, 11, 15))),
                        true,
                        Some DeadlineOutcome.Met
                        ReminderHandling.Completed(CalendarDate.ofDate (DateOnly(2026, 11, 16))),
                        false,
                        Some DeadlineOutcome.Met
                        ReminderHandling.Completed(CalendarDate.ofDate (DateOnly(2026, 11, 17))),
                        false,
                        Some DeadlineOutcome.Missed
                        ReminderHandling.Cancelled, false, Some DeadlineOutcome.Cancelled
                    ] do
                    Expect.equal
                        (DeadlineOutcome.ofTiming overdue (projected handling))
                        expected
                        "Deadline outcome")
            testCase
                "current reminder timing uses its supplied business date and retained state"
                (fun () ->
                    let id =
                        Guid.Parse "20000000-0000-7000-8000-000000000071"
                        |> OperationId.create
                        |> Option.get

                    let parent = CaseId.ofOperationId id

                    let pending =
                        Reminder.decode
                            (ReminderId.ofOperationId id)
                            parent
                            Revision.first
                            (ReminderAction.parse "Follow up" |> required)
                            (CalendarDate.parse "2026-11-01" |> required)
                            ReminderKind.FollowUp
                            ReminderState.Pending
                            None
                        |> required

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
                            Reminder.decode
                                (Reminder.id pending)
                                parent
                                Revision.first
                                (Reminder.action pending)
                                (Reminder.due pending)
                                ReminderKind.FollowUp
                                state
                                (if state = ReminderState.Completed then
                                     Some(CalendarDate.ofDate (System.DateOnly(2026, 11, 18)))
                                 else
                                     None)
                            |> required

                        Expect.equal
                            (ReminderDue.at (DateOnly(2031, 1, 1)) true terminal)
                            None
                            "Terminal reminders are not actionable overdue work")
        ]
