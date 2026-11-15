module ClaimCore.Tests.Tasks.CaseEraseTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module Erase = ClaimCore.Domain.Tasks.Case.Erase

[<Tests>]
let tests =
    testList
        "task case.erase"
        [
            testCase
                "erasure checks the reviewed family sequence including independently changed children"
                (fun () ->
                    let before = family ()
                    let state, _ = reminder ()

                    Erase.decide
                        (context 3)
                        environment
                        (Some state)
                        (Binding.CaseErase(target state, state.Case.Revision, before.Sequence))
                        InputMap.empty
                    |> expectRefusal (Refusal.Case CaseRefusal.FamilyChanged)

                    let changes =
                        Erase.decide
                            (context 3)
                            environment
                            (Some state)
                            (Binding.CaseErase(target state, state.Case.Revision, state.Sequence))
                            InputMap.empty
                        |> accepted

                    Expect.equal
                        changes.Head.Erases
                        (Some(CompartmentOwner.Family state.Id))
                        "Explicit owner clearing authority"

                    let after = apply state changes
                    Expect.equal after.Reference state.Reference "Identity remains reserved"

                    Expect.equal
                        after.Case.Value.Lifecycle
                        Lifecycle.Erased
                        "Retained erased lifecycle"

                    Expect.isNone after.Case.Value.Content "Private business facts removed"

                    Expect.isTrue
                        (after.Reminders |> Map.forall (fun _ part -> part.Value.Plan.IsNone))
                        "Child plans cleared")
            testCase
                "voided families can be erased but erased families cannot change again"
                (fun () ->
                    let state = family () |> setCase Lifecycle.Voided CaseStatus.Opened

                    let after =
                        Erase.decide
                            (context 3)
                            environment
                            (Some state)
                            (Binding.CaseErase(target state, state.Case.Revision, state.Sequence))
                            InputMap.empty
                        |> accepted
                        |> apply state

                    Erase.decide
                        (context 4)
                        environment
                        (Some after)
                        (Binding.CaseErase(target after, after.Case.Revision, after.Sequence))
                        InputMap.empty
                    |> expectRefusal (Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Erased)))
        ]
