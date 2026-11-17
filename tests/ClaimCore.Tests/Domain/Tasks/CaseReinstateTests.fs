module ClaimCore.Tests.Tasks.CaseReinstateTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module Reinstate = ClaimCore.Domain.Tasks.Case.Reinstate

[<Tests>]
let tests =
    testList
        "task case.reinstate"
        [
            testCase
                "the accepted disposition carries its reason while decision state stays minimal"
                (fun () ->
                    let state = family () |> setCase Lifecycle.Voided CaseStatus.Opened

                    let changes =
                        Reinstate.decide
                            (context 3)
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (caseBinding state)
                            (inputs [ text InputTarget.Reason "Documented reason" ])
                        |> accepted

                    match changes.Head.Value with
                    | PartValue.Case value ->
                        match value.Disposition with
                        | Some(CaseDisposition.Reinstated reason) ->
                            Expect.equal
                                (Reason.format reason)
                                "Documented reason"
                                "Accepted reason is available to review, receipt and history"
                        | _ -> failtest "Expected the accepted disposition"
                    | _ -> failtest "Expected a case value"

                    let after = apply state changes
                    Expect.equal after.Case.Value.Lifecycle Lifecycle.Active "Retained lifecycle"

                    Expect.equal
                        after.Case.Value.Content
                        state.Case.Value.Content
                        "Business facts preserved"

                    Reinstate.decide
                        (context 4)
                        environment
                        (Some(DecisionFacts.fromReplay after))
                        (caseBinding after)
                        (inputs [ text InputTarget.Reason "Again" ])
                    |> expectRefusal (Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Undecided)))
            testCase "current reason admission refuses an empty reason" (fun () ->
                let state = family () |> setCase Lifecycle.Voided CaseStatus.Opened

                let issues =
                    Reinstate.decide
                        (context 3)
                        environment
                        (Some(DecisionFacts.fromReplay state))
                        (caseBinding state)
                        (inputs [ text InputTarget.Reason "" ])
                    |> expectInputs

                Expect.equal
                    issues.Head.Input
                    (TaskInputId.Target InputTarget.Reason)
                    "The reason owns the advice")
        ]
