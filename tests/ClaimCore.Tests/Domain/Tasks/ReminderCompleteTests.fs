module ClaimCore.Tests.Tasks.ReminderCompleteTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module Complete = ClaimCore.Domain.Tasks.Reminder.Complete

[<Tests>]
let tests =
    testList
        "task reminder.complete"
        [
            testCase
                "closed active parents allow terminal work while pending state is required"
                (fun () ->
                    let state, id = reminder ()
                    let state = state |> setCase Lifecycle.Active CaseStatus.Closed

                    let changes =
                        Complete.decide
                            (context 3)
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (reminderBinding state id Revision.first)
                            InputMap.empty
                        |> accepted

                    let after = apply state changes

                    Expect.equal
                        after.Reminders[id].Value.State
                        ReminderState.Completed
                        "Accepted terminal value"

                    Expect.equal
                        after.Reminders[id].Value.Plan
                        state.Reminders[id].Value.Plan
                        "The accepted plan is preserved"

                    Complete.decide
                        (context 4)
                        environment
                        (Some(DecisionFacts.fromReplay after))
                        (reminderBinding after id after.Reminders[id].Revision)
                        InputMap.empty
                    |> expectRefusal (
                        Refusal.Reminder(ReminderRuleRefusal.NotOffered ReminderState.Completed)
                    ))
            testCase "inactive parents, foreign selections and stale revisions refuse" (fun () ->
                let state, id = reminder ()
                let blocked = state |> setCase Lifecycle.Voided CaseStatus.Opened

                Complete.decide
                    (context 3)
                    environment
                    (Some(DecisionFacts.fromReplay blocked))
                    (reminderBinding blocked id Revision.first)
                    InputMap.empty
                |> expectRefusal (Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Voided))

                Complete.decide
                    (context 3)
                    environment
                    (Some(DecisionFacts.fromReplay state))
                    (Binding.Reminder(
                        CaseSelector.Id(CaseId.ofOperationId (operation 9)),
                        id,
                        Revision.first
                    ))
                    InputMap.empty
                |> expectRefusal (Refusal.Access AccessRefusal.ResourceUnavailable)

                Complete.decide
                    (context 3)
                    environment
                    (Some(DecisionFacts.fromReplay state))
                    (reminderBinding state id Revision.zero)
                    InputMap.empty
                |> expectRefusal (Refusal.revisionConflict Revision.first))
        ]
