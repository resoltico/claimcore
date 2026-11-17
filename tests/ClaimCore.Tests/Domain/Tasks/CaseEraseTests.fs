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
            testCase "erasure advances the exact revision bounds and refuses saturation" (fun () ->
                for lifecycle in [ Lifecycle.Active; Lifecycle.Voided ] do
                    for number in [ 1L; 2L; Revision.Maximum - 1L; Revision.Maximum ] do
                        let revision = Revision.create number |> Option.get
                        let sequence = Sequence.create number |> Option.get

                        let initialValue =
                            match initialChange.Value with
                            | PartValue.Case value -> value
                            | _ -> failtest "Expected case fixture"

                        let part =
                            { initialChange with
                                Revision = revision
                                NaturalKey = if number = 1L then initialChange.NaturalKey else None
                                Stamp =
                                    { initialChange.Stamp with
                                        Position = EffectPosition.create sequence 0 |> Option.get
                                        OperationId =
                                            if number = 1L then
                                                initialChange.Stamp.OperationId
                                            else
                                                (context 2).Stamp.OperationId
                                    }
                                Value =
                                    PartValue.Case
                                        { initialValue with
                                            Lifecycle = lifecycle
                                            Reference =
                                                if number = 1L then Some reference else None
                                        }
                            }

                        let state =
                            CaseFamily.restore
                                (family ()).Id
                                reference
                                sequence
                                [ part ]
                                (Map.ofList [ (PartKind.Case, part.PartId), initialChange.Stamp ])
                            |> accepted

                        let nextSequence =
                            if number = Revision.Maximum then
                                sequence
                            else
                                Sequence.successor sequence

                        let current = context 3

                        let current =
                            { current with
                                Stamp =
                                    { current.Stamp with
                                        Position =
                                            EffectPosition.create nextSequence 0 |> Option.get
                                    }
                            }

                        let result =
                            Erase.decide
                                current
                                environment
                                (Some(DecisionFacts.fromReplay state))
                                (Binding.CaseErase(target state, revision, sequence))
                                InputMap.empty

                        if number = Revision.Maximum then
                            result |> expectRefusal (Refusal.revisionConflict revision)
                        else
                            let changes = result |> accepted

                            Expect.equal
                                (Revision.value changes.Head.Revision)
                                (number + 1L)
                                "Exactly one revision at every tested bound"

                            let after = apply state changes

                            Expect.equal
                                after.Case.Value.Lifecycle
                                Lifecycle.Erased
                                "Both active and voided families erase")
            testCase
                "erasure checks the reviewed family sequence including independently changed children"
                (fun () ->
                    let before = family ()
                    let state, _ = reminder ()

                    Erase.decide
                        (context 3)
                        environment
                        (Some(DecisionFacts.fromReplay state))
                        (Binding.CaseErase(target state, state.Case.Revision, before.Sequence))
                        InputMap.empty
                    |> expectRefusal (Refusal.Case CaseRefusal.FamilyChanged)

                    let changes =
                        Erase.decide
                            (context 3)
                            environment
                            (Some(DecisionFacts.fromReplay state))
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
                            (Some(DecisionFacts.fromReplay state))
                            (Binding.CaseErase(target state, state.Case.Revision, state.Sequence))
                            InputMap.empty
                        |> accepted
                        |> apply state

                    Erase.decide
                        (context 4)
                        environment
                        (Some(DecisionFacts.fromReplay after))
                        (Binding.CaseErase(target after, after.Case.Revision, after.Sequence))
                        InputMap.empty
                    |> expectRefusal (Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Erased)))
        ]
