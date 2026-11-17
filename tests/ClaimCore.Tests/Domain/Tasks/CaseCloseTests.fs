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
                            (Some state)
                            (caseBinding state)
                            InputMap.empty
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
                        (Some after)
                        (caseBinding after)
                        InputMap.empty
                    |> expectRefusal (Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Closed)))
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
                        (Some state)
                        (caseBinding state)
                        InputMap.empty
                    |> expectRefusal (Refusal.Case(CaseRefusal.NotOffered headline)))
        ]
