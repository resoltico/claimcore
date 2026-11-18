module ClaimCore.Tests.Tasks.CaseRecordPaymentTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Tests.TaskBuilders

module RecordPayment = ClaimCore.Domain.Tasks.Case.RecordPayment

[<Tests>]
let tests =
    testList
        "task case.recordPayment"
        [
            testCase "an advance payment keeps its date and currency without a decision" (fun () ->
                let state = family ()

                let after =
                    RecordPayment.decide
                        (context 2)
                        environment
                        (Some(DecisionFacts.fromReplay state))
                        (caseBinding state)
                        (paymentInputs "2025-10-01" "50" "USD")
                    |> accepted
                    |> apply state

                let entry = after.Payments[operation 2].Value.Entry.Value

                Expect.equal
                    (PaymentEntry.direction entry)
                    PaymentDirection.Paid
                    "Declared default"

                Expect.equal
                    (PaymentEntry.date entry |> CalendarDate.format)
                    "2025-10-01"
                    "Authored date"

                Expect.equal (PaymentEntry.money entry |> Money.currency) "USD" "Entry currency"
                Expect.isNone after.Case.Value.Content.Value.Finance.Decision "Still undecided")
            testCase
                "payment and financial case values are one ordered decision with exact totals"
                (fun () ->
                    let state = decided ()

                    let changes =
                        RecordPayment.decide
                            (context 3)
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (caseBinding state)
                            (paymentInputs "2026-02-02" "25.1234" "EUR")
                        |> accepted

                    Expect.equal
                        (changes |> List.map _.Kind)
                        [ PartKind.Payment; PartKind.Case ]
                        "Accepted entry precedes the dependent financial state"

                    let after = apply state changes
                    let finance = after.Case.Value.Content |> Option.get |> _.Finance

                    Expect.equal
                        (ClaimCore.TestSupport.CaseFixtures.netPaid "EUR" finance)
                        "25.1234"
                        "No decimal loss"

                    Expect.equal finance.PaymentCount 1L "One active entry"

                    Expect.equal
                        (after.Payments
                         |> Map.find (operation 3)
                         |> _.Value.Entry
                         |> Option.map (PaymentEntry.amount >> Amount.value))
                        (Some 25.1234M)
                        "Accepted entry values")
            testCase "active payment count may cross fifty and each effect stays bounded" (fun () ->
                let after =
                    [ 3..53 ]
                    |> List.fold
                        (fun state number -> pay number "2026-02-02" "1" state)
                        (decided ())

                let finance = after.Case.Value.Content |> Option.get |> _.Finance
                Expect.equal finance.PaymentCount 51L "Fifty is a page size, not a business cap"

                Expect.equal
                    (ClaimCore.TestSupport.CaseFixtures.netPaid "EUR" finance)
                    "51"
                    "All active entries contribute"

                Expect.equal after.Payments.Count 51 "Every accepted entry retains identity")
            testCase "future dates and nonpositive amounts refuse" (fun () ->
                let state = decided ()

                for date, amount, currency, violation in
                    [
                        "2026-11-16", "1", "EUR", Violation.FutureDate
                        "2026-02-02", "0", "EUR", Violation.PaymentAmountPositive
                    ] do
                    let issues =
                        RecordPayment.decide
                            (context 3)
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (caseBinding state)
                            (paymentInputs date amount currency)
                        |> expectInputs

                    Expect.isTrue
                        (issues |> List.exists (fun issue -> issue.Violation = violation))
                        "The intended rule rejects the input")
            testCase
                "current date advice precedes state eligibility after a valid reviewed revision"
                (fun () ->
                    let state = family ()

                    let issues =
                        RecordPayment.decide
                            (context 2)
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (caseBinding state)
                            (paymentInputs "2026-11-16" "1" "EUR")
                        |> expectInputs

                    Expect.isTrue
                        (issues
                         |> List.exists (fun issue -> issue.Violation = Violation.FutureDate))
                        "Current admission holds on undecided cases")
            testCase "a removed entry identity remains reserved" (fun () ->
                let state = decided () |> pay 3 "2026-02-02" "25"

                let removed =
                    ClaimCore.Domain.Tasks.Case.RemovePayment.decide
                        (context 4)
                        environment
                        (Some(DecisionFacts.fromReplay state))
                        (Binding.CasePayment(target state, state.Case.Revision, operation 3))
                        InputMap.empty
                    |> accepted
                    |> apply state

                RecordPayment.decide
                    (context 3)
                    environment
                    (Some(DecisionFacts.fromReplay removed))
                    (caseBinding removed)
                    (paymentInputs "2026-02-03" "1" "EUR")
                |> expectRefusal Refusal.IdempotencyConflict)
            testCase
                "a previous payment date does not admit a new entry after date rollback"
                (fun () ->
                    let state = decided () |> pay 3 "2026-02-02" "25"
                    let originalContext = context 4

                    let early =
                        { originalContext with
                            Decision =
                                { originalContext.Decision with
                                    BusinessDate = Some(System.DateOnly(2026, 2, 1))
                                }
                        }

                    let issues =
                        RecordPayment.decide
                            early
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            (caseBinding state)
                            (paymentInputs "2026-02-02" "1" "EUR")
                        |> expectInputs

                    Expect.isTrue
                        (issues
                         |> List.exists (fun issue -> issue.Violation = Violation.FutureDate))
                        "Every new payment asserts its own date")
        ]
