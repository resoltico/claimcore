module ClaimCore.Tests.Tasks.WorkUpdateTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module Update = ClaimCore.Domain.Tasks.Work.Update

[<Tests>]
let tests =
    testList
        "task work.update"
        [
            testCase "omission preserves values and explicit null clears them" (fun () ->
                let state = family ()

                let values =
                    inputs
                        [
                            text
                                (InputTarget.Field BusinessField.ResponsibleActor)
                                (ActorId.format actorId)
                            text (InputTarget.Field BusinessField.HandlingSummary) "Summary"
                        ]

                let changes =
                    Update.decide
                        (context 2)
                        environment
                        (Some state)
                        (Binding.Work(target state, Revision.zero))
                        values
                    |> accepted

                let state = apply state changes

                let clear =
                    inputs
                        [
                            TaskInputId.Target(InputTarget.Field BusinessField.HandlingSummary),
                            InputValue.Null
                        ]

                let changes =
                    Update.decide
                        (context 3)
                        environment
                        (Some state)
                        (Binding.Work(target state, Revision.first))
                        clear
                    |> accepted

                let after = apply state changes
                let work = after.Work |> Option.get |> _.Value |> Option.get
                Expect.equal work.Responsible (Some actorId) "Omission preserves responsibility"
                Expect.equal work.Summary None "Null clears the summary"
                Expect.equal after.Case.Revision Revision.first "The work revision is independent")
            testCase "closed cases allow summary edits and refuse assignment changes" (fun () ->
                let state = family () |> setCase Lifecycle.Active CaseStatus.Closed
                let binding = Binding.Work(target state, Revision.zero)

                Expect.isOk
                    (Update.decide
                        (context 3)
                        environment
                        (Some state)
                        binding
                        (inputs
                            [
                                text
                                    (InputTarget.Field BusinessField.HandlingSummary)
                                    "After closing"
                            ]))
                    "Summary stays editable"

                Update.decide
                    (context 3)
                    environment
                    (Some state)
                    binding
                    (inputs
                        [
                            text
                                (InputTarget.Field BusinessField.ResponsibleActor)
                                (ActorId.format actorId)
                        ])
                |> expectRefusal (Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Closed))

                let offer = Update.availability environment (Some state) binding

                Expect.isFalse
                    (List.contains
                        (TaskInputId.Target(InputTarget.Field BusinessField.ResponsibleActor))
                        offer.Editable)
                    "Core owns editable selection")
            testCase
                "eligibility, stale revisions, empty updates and unchanged updates refuse"
                (fun () ->
                    let state = family ()
                    let binding = Binding.Work(target state, Revision.zero)

                    let assignment =
                        inputs
                            [
                                text
                                    (InputTarget.Field BusinessField.ResponsibleActor)
                                    (ActorId.format actorId)
                            ]

                    Update.decide
                        (context 2)
                        { environment with
                            AssigneeEligible = fun _ -> false
                        }
                        (Some state)
                        binding
                        assignment
                    |> expectRefusal (Refusal.Case CaseRefusal.AssigneeUnavailable)

                    Expect.isNonEmpty
                        (Update.decide (context 2) environment (Some state) binding InputMap.empty
                         |> expectInputs)
                        "No authored change"

                    let state =
                        Update.decide (context 2) environment (Some state) binding assignment
                        |> accepted
                        |> apply state

                    Update.decide (context 3) environment (Some state) binding assignment
                    |> expectRefusal (Refusal.revisionConflict Revision.first)

                    Update.decide
                        (context 3)
                        environment
                        (Some state)
                        (Binding.Work(target state, Revision.first))
                        assignment
                    |> expectRefusal (Refusal.Case CaseRefusal.WorkNoChanges))
            testCase
                "permission uses the actual requester and maintenance in the action snapshot"
                (fun () ->
                    let state = family ()
                    let binding = Binding.Work(target state, Revision.zero)

                    let values =
                        inputs [ text (InputTarget.Field BusinessField.HandlingSummary) "Summary" ]

                    Update.decide
                        (context 2)
                        { environment with
                            Requester = AccessView.empty
                        }
                        (Some state)
                        binding
                        values
                    |> expectRefusal (Refusal.Access AccessRefusal.ResourceUnavailable)

                    Update.decide
                        (context 2)
                        { environment with Maintenance = true }
                        (Some state)
                        binding
                        values
                    |> expectRefusal Refusal.RegisterMaintenance

                    let foreign = PrincipalKey.human "https://issuer.example/" "other" |> accepted
                    let context = context 2

                    Update.decide
                        { context with
                            Decision =
                                { context.Decision with
                                    Principal = foreign
                                }
                        }
                        environment
                        (Some state)
                        binding
                        values
                    |> expectRefusal (Refusal.Access AccessRefusal.ResourceUnavailable))
        ]
