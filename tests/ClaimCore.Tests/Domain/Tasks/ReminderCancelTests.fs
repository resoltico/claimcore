module ClaimCore.Tests.Tasks.ReminderCancelTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module Cancel = ClaimCore.Domain.Tasks.Reminder.Cancel

[<Tests>]
let tests =
    testList
        "task reminder.cancel"
        [
            testCase
                "closed active parents allow terminal work while pending state is required"
                (fun () ->
                    let state, id = reminder ()
                    let state = state |> setCase Lifecycle.Active CaseStatus.Closed

                    let changes =
                        Cancel.decide
                            (context 3)
                            environment
                            (Some state)
                            (reminderBinding state id Revision.first)
                            InputMap.empty
                        |> accepted

                    let after = apply state changes

                    Expect.equal
                        after.Reminders[id].Value.State
                        ReminderState.Cancelled
                        "Accepted terminal value"

                    Expect.equal
                        after.Reminders[id].Value.Plan
                        state.Reminders[id].Value.Plan
                        "The accepted plan is preserved"

                    Cancel.decide
                        (context 4)
                        environment
                        (Some after)
                        (reminderBinding after id after.Reminders[id].Revision)
                        InputMap.empty
                    |> expectRefusal (
                        Refusal.Reminder(ReminderRuleRefusal.NotOffered ReminderState.Cancelled)
                    ))
            testCase "inactive parents, foreign selections and stale revisions refuse" (fun () ->
                let state, id = reminder ()
                let blocked = state |> setCase Lifecycle.Voided CaseStatus.Opened

                Cancel.decide
                    (context 3)
                    environment
                    (Some blocked)
                    (reminderBinding blocked id Revision.first)
                    InputMap.empty
                |> expectRefusal (Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Voided))

                Cancel.decide
                    (context 3)
                    environment
                    (Some state)
                    (Binding.Reminder(
                        CaseSelector.Id(CaseId.ofOperationId (operation 9)),
                        id,
                        Revision.first
                    ))
                    InputMap.empty
                |> expectRefusal (Refusal.Access AccessRefusal.ResourceUnavailable)

                Cancel.decide
                    (context 3)
                    environment
                    (Some state)
                    (reminderBinding state id Revision.zero)
                    InputMap.empty
                |> expectRefusal (Refusal.revisionConflict Revision.first))
        ]
