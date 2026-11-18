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
            testCase "deadline kind and actual business completion date are recorded" (fun () ->
                let state = family ()

                let initial =
                    inputs
                        [
                            text InputTarget.ReminderAction "Review dated evidence"
                            text InputTarget.ReminderDueDate "2026-11-16"
                            text InputTarget.ReminderPlanKind "DEADLINE"
                        ]

                let plan =
                    ClaimCore.Domain.Tasks.Reminder.Create.decide
                        (context 2)
                        environment
                        (Some(DecisionFacts.fromReplay state))
                        (Binding.ReminderNew(target state))
                        initial
                    |> accepted
                    |> apply state

                let defaultFamily, defaultReminder = reminder ()

                Expect.equal
                    (defaultFamily.Reminders[defaultReminder].Value.Plan |> Option.map _.Kind)
                    (Some ReminderKind.FollowUp)
                    "An omitted kind creates a follow-up"

                let id = ReminderId.ofOperationId (operation 2)

                Expect.equal
                    (plan.Reminders[id].Value.Plan |> Option.map _.Kind)
                    (Some ReminderKind.Deadline)
                    "Creation independently accepts a deadline"

                let supplied = context 3

                let finish =
                    { supplied with
                        Decision =
                            { supplied.Decision with
                                BusinessDate = Some(System.DateOnly(2026, 11, 18))
                            }
                    }

                let complete submitted =
                    ClaimCore.Domain.Tasks.Reminder.Complete.decide
                        finish
                        environment
                        (Some(DecisionFacts.fromReplay plan))
                        (reminderBinding plan id Revision.first)
                        submitted

                for submission, expected in
                    [
                        InputMap.empty, "2026-11-18"
                        inputs [ text InputTarget.ReminderDoneOn "2026-11-15" ], "2026-11-15"
                    ] do
                    let completed = complete submission |> accepted |> apply plan

                    Expect.equal
                        (completed.Reminders[id].Value.DoneOn |> Option.map CalendarDate.format)
                        (Some expected)
                        "Only completion carries the actual date"

                let rejected =
                    complete (inputs [ text InputTarget.ReminderDoneOn "2026-11-19" ])
                    |> expectInputs

                Expect.equal
                    (rejected |> List.map (fun issue -> issue.Input, issue.Violation))
                    [ TaskInputId.Target InputTarget.ReminderDoneOn, Violation.FutureDate ]
                    "A future date is reported on the submitted completion field")
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
                                ClaimCore.Domain.Changes.PartValue.Reminder(
                                    ReminderValue.Available(
                                        {
                                            Action = oldAction
                                            Due = CalendarDate.parse "2026-11-16" |> accepted
                                            Kind = ReminderKind.FollowUp
                                        },
                                        ReminderHandling.Pending
                                    )
                                )
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
