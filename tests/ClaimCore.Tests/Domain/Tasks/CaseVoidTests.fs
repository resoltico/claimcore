module ClaimCore.Tests.Tasks.CaseVoidTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module Void = ClaimCore.Domain.Tasks.Case.Void

[<Tests>]
let tests =
    testList
        "task case.void"
        [
            testCase
                "the accepted disposition carries its reason while decision state stays minimal"
                (fun () ->
                    let state = family ()

                    let changes =
                        Void.decide
                            (context 3)
                            environment
                            (Some state)
                            (caseBinding state)
                            (inputs [ text InputTarget.Reason "Documented reason" ])
                        |> accepted

                    match changes.Head.Value with
                    | PartValue.Case value ->
                        match value.Disposition with
                        | Some(CaseDisposition.Voided reason) ->
                            Expect.equal
                                (Reason.format reason)
                                "Documented reason"
                                "Accepted reason is available to review, receipt and history"
                        | _ -> failtest "Expected the accepted disposition"
                    | _ -> failtest "Expected a case value"

                    let after = apply state changes
                    Expect.equal after.Case.Value.Lifecycle Lifecycle.Voided "Retained lifecycle"

                    Expect.equal
                        after.Case.Value.Content
                        state.Case.Value.Content
                        "Business facts preserved"

                    Void.decide
                        (context 4)
                        environment
                        (Some after)
                        (caseBinding after)
                        (inputs [ text InputTarget.Reason "Again" ])
                    |> expectRefusal (Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Voided)))
            testCase "current reason admission refuses an empty reason" (fun () ->
                let state = family ()

                let issues =
                    Void.decide
                        (context 3)
                        environment
                        (Some state)
                        (caseBinding state)
                        (inputs [ text InputTarget.Reason "" ])
                    |> expectInputs

                Expect.equal
                    issues.Head.Input
                    (TaskInputId.Target InputTarget.Reason)
                    "The reason owns the advice")
        ]
