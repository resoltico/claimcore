module ClaimCore.Tests.Tasks.ReminderCompleteAndPlanNextTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module CompleteAndPlanNext = ClaimCore.Domain.Tasks.Reminder.CompleteAndPlanNext

[<Tests>]
let tests =
    testList
        "task reminder.completeAndPlanNext"
        [
            testCase
                "one decision completes the selected part and creates the next with ordered attribution"
                (fun () ->
                    let state, id = reminder ()

                    let changes =
                        CompleteAndPlanNext.decide
                            (context 3)
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (reminderBinding state id Revision.first)
                            (planInputs "Review photographs" "2026-11-20")
                        |> accepted

                    Expect.equal changes.Length 2 "A fixed pair is returned together"

                    Expect.equal
                        (changes
                         |> List.map (fun change -> EffectPosition.index change.Stamp.Position))
                        [ 0; 1 ]
                        "Distinct ordered effects"

                    let after = apply state changes

                    Expect.equal
                        after.Reminders[id].Value.State
                        ReminderState.Completed
                        "Original completed"

                    let next = after.Reminders[ReminderId.ofOperationId (operation 3)]
                    Expect.equal next.Revision Revision.first "Next has a new chain"
                    Expect.equal next.Value.State ReminderState.Pending "Next remains pending"

                    Expect.equal
                        (next.Value.Plan
                         |> Option.map (fun plan -> ReminderAction.format plan.Action))
                        (Some "Review photographs")
                        "Next accepted action")
            testCase "a reserved next identity refuses the whole pair" (fun () ->
                let state, id = reminder ()

                CompleteAndPlanNext.decide
                    (context 2)
                    environment
                    (Some(DecisionFacts.fromReplay state))
                    (reminderBinding state id Revision.first)
                    (planInputs "Next" "2026-11-20")
                |> expectRefusal Refusal.IdempotencyConflict

                Expect.equal
                    state.Reminders[id].Value.State
                    ReminderState.Pending
                    "The original immutable state is untouched")
            testCase
                "an invalid replacement yields no compound effects and does not reuse prior action admission"
                (fun () ->
                    let state, id = reminder ()

                    let issues =
                        CompleteAndPlanNext.decide
                            (context 3)
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (reminderBinding state id Revision.first)
                            (planInputs (String.replicate 201 "x") "2026-11-20")
                        |> expectInputs

                    Expect.isTrue
                        (issues
                         |> List.exists (fun issue -> issue.Violation = Violation.TooLong 200))
                        "The new entity requires current admission"

                    Expect.equal
                        state.Reminders[id].Value.State
                        ReminderState.Pending
                        "No primary completion escapes the failed pair"

                    Expect.equal state.Reminders.Count 1 "No next reminder escapes")
        ]
