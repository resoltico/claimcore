module ClaimCore.Tests.Tasks.ReminderCreateTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module Create = ClaimCore.Domain.Tasks.Reminder.Create

[<Tests>]
let tests =
    testList
        "task reminder.create"
        [
            testCase
                "creation owns an independent identity and revision even when due in the past"
                (fun () ->
                    let state = family ()

                    let changes =
                        Create.decide
                            (context 2)
                            environment
                            (Some state)
                            (Binding.ReminderNew(target state))
                            (planInputs "Request photographs" "2026-11-01")
                        |> accepted

                    let after = apply state changes
                    let part = after.Reminders[ReminderId.ofOperationId (operation 2)]
                    Expect.equal part.Revision Revision.first "First reminder version"

                    Expect.equal
                        part.Value.State
                        ReminderState.Pending
                        "Time does not complete a reminder"

                    Expect.equal
                        (part.Value.Plan |> Option.map (fun plan -> CalendarDate.format plan.Due))
                        (Some "2026-11-01")
                        "Exact due date"

                    Expect.equal
                        after.Case.Revision
                        Revision.first
                        "Parent concurrency is unchanged")
            testCase "closed voided erased and reserved targets refuse" (fun () ->
                for lifecycle, status, headline in
                    [
                        Lifecycle.Active, CaseStatus.Closed, CaseHeadline.Closed
                        Lifecycle.Voided, CaseStatus.Opened, CaseHeadline.Voided
                        Lifecycle.Erased, CaseStatus.Opened, CaseHeadline.Erased
                    ] do
                    let state = family () |> setCase lifecycle status

                    Create.decide
                        (context 3)
                        environment
                        (Some state)
                        (Binding.ReminderNew(target state))
                        (planInputs "Action" "2026-11-16")
                    |> expectRefusal (Refusal.Case(CaseRefusal.NotOffered headline))

                let state, _ = reminder ()

                Create.decide
                    (context 2)
                    environment
                    (Some state)
                    (Binding.ReminderNew(target state))
                    (planInputs "Action" "2026-11-16")
                |> expectRefusal Refusal.IdempotencyConflict)
            testCase "independent action and due syntax issues accumulate" (fun () ->
                let issues = Create.check (planInputs "" "not-a-date")

                Expect.equal
                    (issues |> List.map _.Input |> Set.ofList)
                    (Set.ofList
                        [
                            TaskInputId.Target InputTarget.ReminderAction
                            TaskInputId.Target InputTarget.ReminderDueDate
                        ])
                    "Both authored controls are diagnosed")
        ]
