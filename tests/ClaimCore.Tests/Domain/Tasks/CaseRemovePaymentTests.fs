module ClaimCore.Tests.Tasks.CaseRemovePaymentTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module Remove = ClaimCore.Domain.Tasks.Case.RemovePayment

[<Tests>]
let tests =
    testList
        "task case.removePayment"
        [
            testCase "removal retains entry identity and recalculates exact totals" (fun () ->
                let state = decided () |> pay 3 "2026-02-02" "25.50" |> pay 4 "2026-02-03" "10"

                let binding = Binding.CasePayment(target state, state.Case.Revision, operation 3)

                let after =
                    Remove.decide
                        (context 5)
                        environment
                        (Some(DecisionFacts.fromReplay state))
                        binding
                        InputMap.empty
                    |> accepted
                    |> apply state

                Expect.isTrue
                    after.Payments[operation 3].Value.Removed
                    "The entry is a reserved tombstone"

                Expect.isSome
                    after.Payments[operation 3].Value.Entry
                    "Accepted entry values remain available until family erasure"

                let finance = after.Case.Value.Content |> Option.get |> _.Finance

                Expect.equal
                    (ClaimCore.TestSupport.CaseFixtures.netPaid "EUR" finance)
                    "10"
                    "Exact subtraction"

                Expect.equal finance.PaymentCount 1L "One active entry remains"

                Remove.decide
                    (context 6)
                    environment
                    (Some(DecisionFacts.fromReplay after))
                    (Binding.CasePayment(target after, after.Case.Revision, operation 3))
                    InputMap.empty
                |> expectRefusal (Refusal.Case CaseRefusal.PaymentNotFound))
            testCase
                "removing the last payment leaves the decision and empty financial position"
                (fun () ->
                    let state = decided () |> pay 3 "2026-02-02" "25"

                    let after =
                        Remove.decide
                            (context 4)
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (Binding.CasePayment(target state, state.Case.Revision, operation 3))
                            InputMap.empty
                        |> accepted
                        |> apply state

                    let finance = after.Case.Value.Content |> Option.get |> _.Finance
                    Expect.equal finance.PaymentCount 0L "No active payments"

                    Expect.equal
                        (ClaimCore.TestSupport.CaseFixtures.netPaid "EUR" finance)
                        "0"
                        "Zero paid"

                    Expect.isTrue finance.CurrencyTotals.IsEmpty "No fabricated currency bucket"
                    Expect.isSome finance.Decision "The decision is independent of payment removal")
            testCase
                "an empty payment collection refuses removal at its actual financial state"
                (fun () ->
                    let state = decided ()

                    Remove.decide
                        (context 3)
                        environment
                        (Some(DecisionFacts.fromReplay state))
                        (Binding.CasePayment(target state, state.Case.Revision, operation 9))
                        InputMap.empty
                    |> expectRefusal (Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Decided)))
        ]
