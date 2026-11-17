module ClaimCore.Tests.Tasks.ReminderUpdateTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module Update = ClaimCore.Domain.Tasks.Reminder.Update

[<Tests>]
let tests =
    testList
        "task reminder.update"
        [
            testCase
                "an edit preserves identity and consumes only its selected reminder revision"
                (fun () ->
                    let state, id = reminder ()

                    let changes =
                        Update.decide
                            (context 3)
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (reminderBinding state id Revision.first)
                            (planInputs "Review photographs" "2026-11-20")
                        |> accepted

                    let after = apply state changes

                    Expect.equal
                        after.Reminders[id].Revision
                        (Revision.create 2L |> Option.get)
                        "Independent version"

                    Expect.equal
                        after.Case.Revision
                        Revision.first
                        "Parent version remains unchanged"

                    Expect.equal
                        (after.Reminders[id].Value.Plan
                         |> Option.map (fun plan -> ReminderAction.format plan.Action))
                        (Some "Review photographs")
                        "Accepted action"

                    Update.decide
                        (context 4)
                        environment
                        (Some(DecisionFacts.fromReplay after))
                        (reminderBinding after id Revision.first)
                        (planInputs "Another" "2026-11-21")
                    |> expectRefusal (Refusal.revisionConflict after.Reminders[id].Revision))
            testCase "unchanged edits do not produce events" (fun () ->
                let state, id = reminder ()

                Update.decide
                    (context 3)
                    environment
                    (Some(DecisionFacts.fromReplay state))
                    (reminderBinding state id Revision.first)
                    (planInputs "Request photographs" "2026-11-16")
                |> expectRefusal (Refusal.Reminder ReminderRuleRefusal.NoChanges))
            testCase "availability selects the reminder named by the binding" (fun () ->
                let state, id = reminder ()

                let completed =
                    ClaimCore.Domain.Tasks.Reminder.Complete.decide
                        (context 3)
                        environment
                        (Some(DecisionFacts.fromReplay state))
                        (reminderBinding state id Revision.first)
                        InputMap.empty
                    |> accepted
                    |> apply state

                let changes =
                    ClaimCore.Domain.Tasks.Reminder.Create.decide
                        (context 4)
                        environment
                        (Some(DecisionFacts.fromReplay completed))
                        (Binding.ReminderNew(target completed))
                        (planInputs "Other reminder" "2026-11-19")
                    |> accepted

                let state = apply completed changes
                let pendingId = ReminderId.ofOperationId (operation 4)

                Expect.isSome
                    (Update.availability
                        environment
                        (Some(DecisionFacts.fromReplay state))
                        (reminderBinding state id (Revision.create 2L |> Option.get)))
                        .Blocked
                    "The completed row blocks edits"

                Expect.isNone
                    (Update.availability
                        environment
                        (Some(DecisionFacts.fromReplay state))
                        (reminderBinding state pendingId Revision.first))
                        .Blocked
                    "The pending row offers edits")
            testCase
                "unchanged retained actions can be rescheduled while new actions use current admission"
                (fun () ->
                    let state, id = reminder ()
                    let oldAction = ReminderAction.parse (String.replicate 201 "x") |> accepted

                    let retainedChange =
                        { initialChange with
                            PartId = ReminderId.value id
                            Revision = Revision.create 2L |> Option.get
                            NaturalKey = None
                            Stamp = (context 3).Stamp
                            Value =
                                ClaimCore.Domain.Changes.PartValue.Reminder
                                    {
                                        State = ReminderState.Pending
                                        Plan =
                                            Some
                                                {
                                                    Action = oldAction
                                                    Due =
                                                        CalendarDate.parse "2026-11-16" |> accepted
                                                }
                                    }
                        }

                    let state = apply state [ retainedChange ]

                    let changes =
                        Update.decide
                            (context 4)
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (reminderBinding state id state.Reminders[id].Revision)
                            (planInputs (ReminderAction.format oldAction) "2026-11-20")
                        |> accepted

                    let after = apply state changes

                    Expect.equal
                        (after.Reminders[id].Value.Plan |> Option.map _.Action)
                        (Some oldAction)
                        "An unchanged accepted action keeps its meaning"

                    let changed =
                        Update.decide
                            (context 4)
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (reminderBinding state id state.Reminders[id].Revision)
                            (planInputs (String.replicate 201 "y") "2026-11-20")
                        |> expectInputs

                    Expect.isTrue
                        (changed
                         |> List.exists (fun issue -> issue.Violation = Violation.TooLong 200))
                        "A replacement is a new assertion")
        ]
