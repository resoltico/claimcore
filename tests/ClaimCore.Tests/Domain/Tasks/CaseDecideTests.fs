module ClaimCore.Tests.Tasks.CaseDecideTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module Decide = ClaimCore.Domain.Tasks.Case.Decide

[<Tests>]
let tests =
    testList
        "task case.decide"
        [
            testCase
                "decisions can be replaced while accepted payments constrain currency and amount"
                (fun () ->
                    let state = decided () |> pay 3 "2026-02-02" "25.50"

                    let after =
                        Decide.decide
                            (context 4)
                            environment
                            (Some state)
                            (caseBinding state)
                            (decisionInputs "2026-02-01" "1200" "EUR")
                        |> accepted
                        |> apply state

                    let finance = after.Case.Value.Content |> Option.get |> _.Finance

                    Expect.equal
                        (finance.Decision |> Option.map (Decision.payable >> Money.value))
                        (Some 1200M)
                        "Accepted replacement"

                    Expect.equal
                        (Amount.value finance.PaidAmount)
                        25.50M
                        "The existing payment remains exact"

                    let currency =
                        Decide.decide
                            (context 4)
                            environment
                            (Some state)
                            (caseBinding state)
                            (decisionInputs "2026-02-01" "1000" "USD")
                        |> expectInputs

                    Expect.isTrue
                        (currency
                         |> List.exists (fun issue ->
                             issue.Violation = Violation.PaymentCurrencyMismatch))
                        "Paid currency cannot change"

                    let amount =
                        Decide.decide
                            (context 4)
                            environment
                            (Some state)
                            (caseBinding state)
                            (decisionInputs "2026-02-01" "20" "EUR")
                        |> expectInputs

                    Expect.isTrue
                        (amount
                         |> List.exists (fun issue ->
                             issue.Violation = Violation.PaymentExceedsPayable))
                        "Payable cannot fall below paid")
            testCase "independent malformed money and chronology controls accumulate" (fun () ->
                let state = family ()

                let issues =
                    Decide.decide
                        (context 2)
                        environment
                        (Some state)
                        (caseBinding state)
                        (decisionInputs "2026-01-01" "not-money" "EUR")
                    |> expectInputs

                Expect.isTrue
                    (issues
                     |> List.exists (fun issue ->
                         issue.Input =
                             TaskInputId.Target(InputTarget.Field BusinessField.PayableAmount)))
                    "Money issue"

                Expect.isTrue
                    (issues
                     |> List.exists (fun issue ->
                         issue.RelatedDate
                         |> Option.exists (fun related ->
                             related.Field = BusinessField.IncidentNotificationDate)))
                    "Chronology survives incomplete money")
            testCase "malformed syntax keeps complete input advice ahead of stale state" (fun () ->
                let state = decided ()

                let issues =
                    Decide.decide
                        (context 3)
                        environment
                        (Some state)
                        (Binding.Case(target state, Revision.zero))
                        (decisionInputs "not-a-date" "bad-money" "EUR")
                    |> expectInputs

                Expect.equal
                    (issues |> List.map _.Input)
                    [
                        TaskInputId.Target(InputTarget.Field BusinessField.PaymentDecisionDate)
                        TaskInputId.Target(InputTarget.Field BusinessField.PayableAmount)
                    ]
                    "Both malformed controls survive the stale revision"

                Decide.decide
                    (context 3)
                    environment
                    (Some state)
                    (Binding.Case(target state, Revision.zero))
                    (decisionInputs "2026-11-16" "1000" "EUR")
                |> expectRefusal (Refusal.revisionConflict state.Case.Revision))
            testCase
                "claim and payable currencies are independent and zero does not invent a payment"
                (fun () ->
                    let state = family ()

                    let after =
                        Decide.decide
                            (context 2)
                            environment
                            (Some state)
                            (caseBinding state)
                            (decisionInputs "2026-02-01" "1500" "USD")
                        |> accepted
                        |> apply state

                    let content = after.Case.Value.Content |> Option.get

                    Expect.equal
                        (Money.currency (Registration.claimed content.Registration))
                        "EUR"
                        "The original claim is unchanged"

                    Expect.equal
                        (content.Finance.Decision
                         |> Option.map (Decision.payable >> Money.currency))
                        (Some "USD")
                        "Decision currency is its own fact"

                    let zero =
                        Decide.decide
                            (context 2)
                            environment
                            (Some state)
                            (caseBinding state)
                            (decisionInputs "2026-02-01" "0" "EUR")
                        |> accepted
                        |> apply state

                    let finance = zero.Case.Value.Content |> Option.get |> _.Finance
                    Expect.equal finance.PaymentCount 0L "No fictitious transfer"
                    Expect.isNone finance.PaymentDates "No invented payment date"
                    Expect.isEmpty zero.Payments "No payment part is created")
            testCase
                "decision-date reaffirmation survives rollback but a changed or withdrawn date is new"
                (fun () ->
                    let state = decided ()
                    let originalContext = context 3

                    let early =
                        { originalContext with
                            Decision =
                                { originalContext.Decision with
                                    BusinessDate = Some(System.DateOnly(2026, 1, 15))
                                }
                        }

                    let after =
                        Decide.decide
                            early
                            environment
                            (Some state)
                            (caseBinding state)
                            (decisionInputs "2026-02-01" "1200" "EUR")
                        |> accepted
                        |> apply state

                    Expect.equal
                        (after.Case.Value.Content
                         |> Option.bind (fun content -> content.Finance.Decision)
                         |> Option.map (Decision.date >> CalendarDate.format))
                        (Some "2026-02-01")
                        "Accepted date retained"

                    let changed =
                        Decide.decide
                            early
                            environment
                            (Some state)
                            (caseBinding state)
                            (decisionInputs "2026-02-02" "1200" "EUR")
                        |> expectInputs

                    Expect.isTrue
                        (changed
                         |> List.exists (fun issue -> issue.Violation = Violation.FutureDate))
                        "A different date is a new assertion"

                    let withdrawn =
                        ClaimCore.Domain.Tasks.Case.WithdrawDecision.decide
                            (context 3)
                            environment
                            (Some state)
                            (caseBinding state)
                            InputMap.empty
                        |> accepted
                        |> apply state

                    let issues =
                        Decide.decide
                            early
                            environment
                            (Some withdrawn)
                            (caseBinding withdrawn)
                            (decisionInputs "2026-02-01" "1000" "EUR")
                        |> expectInputs

                    Expect.isTrue
                        (issues
                         |> List.exists (fun issue -> issue.Violation = Violation.FutureDate))
                        "Withdrawal does not leave a date reaffirmation exemption")
        ]
