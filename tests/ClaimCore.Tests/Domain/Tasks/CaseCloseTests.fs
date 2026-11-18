module ClaimCore.Tests.Tasks.CaseCloseTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module Close = ClaimCore.Domain.Tasks.Case.Close

[<Tests>]
let tests =
    testList
        "task case.close"
        [
            testCase
                "the status transition preserves private business facts and rejects repetition"
                (fun () ->
                    let state = family ()

                    let after =
                        Close.decide
                            (context 3)
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (caseBinding state)
                            (ClaimCore.Tests.TaskBuilders.closingInputs "SETTLED")
                        |> accepted
                        |> apply state

                    Expect.equal after.Case.Value.Status CaseStatus.Closed "Accepted status"

                    Expect.equal
                        after.Case.Value.Content
                        state.Case.Value.Content
                        "Business facts are independent"

                    Close.decide
                        (context 4)
                        environment
                        (Some(DecisionFacts.fromReplay after))
                        (caseBinding after)
                        (ClaimCore.Tests.TaskBuilders.closingInputs "SETTLED")
                    |> expectRefusal (Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Closed)))
            testCase
                "close requires and records the chosen outcome, with core-provided seeds"
                (fun () ->
                    let chosen = TaskInputId.Target(InputTarget.Field BusinessField.Outcome)

                    let offer state =
                        Close.availability
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (caseBinding state)
                        |> _.Seeds
                        |> Map.tryFind chosen

                    let undecided = family ()
                    Expect.isNone (offer undecided) "Undecided has no outcome seed"

                    let decide = ClaimCore.Domain.Tasks.Case.Decide.decide

                    let zero =
                        decide
                            (context 2)
                            environment
                            (Some(DecisionFacts.fromReplay undecided))
                            (caseBinding undecided)
                            (decisionInputs "2026-02-01" "0" "EUR")
                        |> accepted
                        |> apply undecided

                    Expect.equal (offer zero) (Some(InputValue.Text "DECLINED")) "Zero payable"

                    let paid = decided () |> pay 3 "2026-02-02" "1000"
                    Expect.equal (offer paid) (Some(InputValue.Text "SETTLED")) "Payable covered"

                    let close input =
                        Close.decide
                            (context 4)
                            environment
                            (Some(DecisionFacts.fromReplay paid))
                            (caseBinding paid)
                            input

                    Expect.equal
                        (close InputMap.empty |> expectInputs |> List.map _.Input)
                        [ chosen ]
                        "An unused seed never supplies an outcome implicitly"

                    let closed = close (closingInputs "WITHDRAWN") |> accepted |> apply paid

                    Expect.equal
                        (Handling.closure closed.Case.Value.Handling |> Option.map _.Outcome)
                        (Some ClosureOutcome.Withdrawn)
                        "The person's assertion is authoritative")
            testCase "voided and erased parents do not offer business status changes" (fun () ->
                for lifecycle, headline in
                    [
                        Lifecycle.Voided, CaseHeadline.Voided
                        Lifecycle.Erased, CaseHeadline.Erased
                    ] do
                    let state = family () |> setCase lifecycle CaseStatus.Opened

                    Close.decide
                        (context 3)
                        environment
                        (Some(DecisionFacts.fromReplay state))
                        (caseBinding state)
                        (ClaimCore.Tests.TaskBuilders.closingInputs "SETTLED")
                    |> expectRefusal (Refusal.Case(CaseRefusal.NotOffered headline)))
        ]
