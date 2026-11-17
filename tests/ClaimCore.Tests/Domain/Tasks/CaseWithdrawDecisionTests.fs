module ClaimCore.Tests.Tasks.CaseWithdrawDecisionTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module Withdraw = ClaimCore.Domain.Tasks.Case.WithdrawDecision

[<Tests>]
let tests =
    testList
        "task case.withdrawDecision"
        [
            testCase
                "withdrawal preserves registration and clears only an unpaid decision"
                (fun () ->
                    let state = decided ()

                    let after =
                        Withdraw.decide
                            (context 3)
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (caseBinding state)
                            InputMap.empty
                        |> accepted
                        |> apply state

                    let content = after.Case.Value.Content |> Option.get
                    Expect.isNone content.Finance.Decision "Undecided again"

                    Expect.equal
                        content.Registration
                        (state.Case.Value.Content |> Option.get).Registration
                        "Registration is unchanged"

                    Expect.equal content.Finance.PaymentCount 0L "No payments fabricated")
            testCase "missing decisions and active payments prevent withdrawal" (fun () ->
                for state, headline in
                    [
                        family (), CaseHeadline.Undecided
                        decided () |> pay 3 "2026-02-02" "1", CaseHeadline.PartlyPaid
                    ] do
                    Withdraw.decide
                        (context 4)
                        environment
                        (Some(DecisionFacts.fromReplay state))
                        (caseBinding state)
                        InputMap.empty
                    |> expectRefusal (Refusal.Case(CaseRefusal.NotOffered headline)))
        ]
