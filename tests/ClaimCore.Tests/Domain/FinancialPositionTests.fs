module ClaimCore.Tests.FinancialPositionTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.TestSupport

let private accepted value =
    Result.defaultWith (fun error -> failtestf "%A" error) value

let private amount value = Amount.parse value |> accepted
let private date value = Inputs.date value

let private bounds first last =
    PaymentDateBounds.create (date first) (date last) |> Option.get

let private decision value currency =
    Inputs.decisionWith
        [
            BusinessField.PaymentDecisionDate, "2026-01-10"
            BusinessField.PayableAmount, value
            BusinessField.PayableCurrency, currency
        ]
        Inputs.decision
    |> Inputs.admittedDecision

let private payment value currency day =
    Inputs.paymentWith
        [
            BusinessField.PaidAmount, value
            BusinessField.PaidCurrency, currency
            BusinessField.PaymentDate, day
        ]
        Inputs.payment
    |> Inputs.admittedPayment

let private position payable paid count dates =
    FinancialPosition.create (Some(decision payable "EUR")) (amount paid) count dates
    |> accepted

let private entry day paid =
    PaymentEntry.create
        (CaseFixtures.operationId (Guid.Parse "10000000-0000-7000-8000-000000000001"))
        (date day)
        (amount paid)
    |> accepted

let private today = DateOnly(2026, 1, 31)

[<Tests>]
let tests =
    testList
        "private financial position"
        [
            testCase "constructors reject contradictory aggregate and boundary facts" (fun () ->
                Expect.isEmpty
                    (typeof<FinancialPosition>.GetConstructors())
                    "Only validated creation is public"

                Expect.isNone
                    (PaymentDateBounds.create (date "2026-01-12") (date "2026-01-11"))
                    "Dates are ordered"

                for paid, count, dates in
                    [
                        "1", 0L, None
                        "0", 1L, Some(bounds "2026-01-11" "2026-01-11")
                        "1", 1L, None
                        "1", 1L, Some(bounds "2026-01-11" "2026-01-12")
                        "0.0001", 2L, Some(bounds "2026-01-11" "2026-01-11")
                        "0", 0L, Some(bounds "2026-01-11" "2026-01-11")
                        "1", -1L, None
                        "1", SafeInteger.Maximum + 1L, None
                    ] do
                    Expect.isError
                        (FinancialPosition.create
                            (Some(decision "10" "EUR"))
                            (amount paid)
                            count
                            dates)
                        "Impossible facts never construct a trusted position"

                Expect.isError
                    (FinancialPosition.create
                        None
                        (amount "1")
                        1L
                        (Some(bounds "2026-01-11" "2026-01-11")))
                    "Payments require a decision"

                Expect.isError
                    (FinancialPosition.create
                        (Some(decision "10" "EUR"))
                        (amount "11")
                        1L
                        (Some(bounds "2026-01-11" "2026-01-11")))
                    "No overpayment"

                Expect.isError
                    (FinancialPosition.create
                        (Some(decision "10" "EUR"))
                        (amount "1")
                        1L
                        (Some(bounds "2026-01-09" "2026-01-09")))
                    "No payment before its decision")
            testCase
                "progress and same-currency totals are derived including zero payable"
                (fun () ->
                    Expect.equal
                        (CaseProgress.ofFinancial FinancialPosition.undecided)
                        CaseProgress.Undecided
                        "Absent decision"

                    Expect.isNone
                        (FinancialPosition.totals FinancialPosition.undecided)
                        "No currency is invented"

                    for payable, paid, count, dates, expected in
                        [
                            "0", "0", 0L, None, CaseProgress.Paid
                            "100", "0", 0L, None, CaseProgress.Decided
                            "100",
                            "40",
                            2L,
                            Some(bounds "2026-01-11" "2026-01-12"),
                            CaseProgress.PartlyPaid
                            "100",
                            "100",
                            2L,
                            Some(bounds "2026-01-11" "2026-01-12"),
                            CaseProgress.Paid
                        ] do
                        let value = position payable paid count dates

                        Expect.equal
                            (CaseProgress.ofFinancial value)
                            expected
                            "No independently stored progress tag"

                        let totals = FinancialPosition.totals value |> Option.get

                        Expect.equal
                            (Money.value totals.Paid + Money.value totals.Remaining)
                            (Money.value totals.Payable)
                            "Exact conservation"

                        Expect.equal
                            (Money.currency totals.Remaining)
                            "EUR"
                            "Decision owns currency")
            testCase "fifty-one entries are representable without a business count cap" (fun () ->
                let start = position "100" "0" 0L None

                let final =
                    [ 1..51 ]
                    |> List.fold
                        (fun current _ ->
                            FinancialPosition.record
                                today
                                (payment "1" "EUR" "2026-01-11")
                                current
                            |> accepted)
                        start

                Expect.equal final.PaymentCount 51L "Count exceeds the removed design limit"
                Expect.equal (Amount.value final.PaidAmount) 51M "Paid total is exact"

                Expect.equal
                    (Money.value ((FinancialPosition.totals final |> Option.get).Remaining))
                    49M
                    "Remaining is derived")
            testCase "record enforces currency payable and newly asserted business dates" (fun () ->
                let start = position "10" "9" 1L (Some(bounds "2026-01-11" "2026-01-11"))

                Expect.equal
                    (FinancialPosition.record today (payment "1" "USD" "2026-01-12") start)
                    (Error FinancialPositionIssue.CurrencyMismatch)
                    "Currency cannot drift"

                Expect.equal
                    (FinancialPosition.record today (payment "2" "EUR" "2026-01-12") start)
                    (Error FinancialPositionIssue.ExceedsPayable)
                    "Bound before addition"

                Expect.equal
                    (FinancialPosition.record today (payment "1" "EUR" "2026-02-01") start)
                    (Error FinancialPositionIssue.FuturePayment)
                    "New dates use supplied business date"

                let acceptedFuture = position "100" "1" 1L (Some(bounds "2026-01-20" "2026-01-20"))

                let earlier =
                    FinancialPosition.record
                        (DateOnly(2026, 1, 15))
                        (payment "1" "EUR" "2026-01-15")
                        acceptedFuture
                    |> accepted

                Expect.equal
                    (earlier.PaymentDates |> Option.map _.Latest)
                    (Some(date "2026-01-20"))
                    "Clock rollback does not re-admit retained dates")
            testCase "technical count saturation refuses without overflow" (fun () ->
                let full =
                    position
                        "999999999999999999"
                        "1000000000000"
                        SafeInteger.Maximum
                        (Some(bounds "2026-01-11" "2026-01-11"))

                Expect.equal
                    (FinancialPosition.record today (payment "1" "EUR" "2026-01-11") full)
                    (Error FinancialPositionIssue.CountOutOfRange)
                    "Exact-integer range is explicit")
            testCase "decision correction and clearing preserve current paid facts" (fun () ->
                let value = position "100" "40" 2L (Some(bounds "2026-01-11" "2026-01-12"))

                Expect.equal
                    (FinancialPosition.withDecision None value)
                    (Error FinancialPositionIssue.DecisionHasPayments)
                    "Cannot clear a paid decision"

                Expect.equal
                    (FinancialPosition.withDecision (Some(decision "100" "USD")) value)
                    (Error FinancialPositionIssue.CurrencyMismatch)
                    "Cannot relabel active entries"

                Expect.equal
                    (FinancialPosition.withDecision (Some(decision "39" "EUR")) value)
                    (Error FinancialPositionIssue.ExceedsPayable)
                    "Cannot reduce below exact paid"

                let corrected =
                    FinancialPosition.withDecision (Some(decision "40" "EUR")) value |> accepted

                Expect.equal
                    (CaseProgress.ofFinancial corrected)
                    CaseProgress.Paid
                    "Progress changes from facts"

                let cleared =
                    FinancialPosition.withDecision None (position "100" "0" 0L None) |> accepted

                Expect.equal
                    cleared
                    FinancialPosition.undecided
                    "No independently retained decision flags")
            testCase
                "sole and extrema removals validate remaining context including ties"
                (fun () ->
                    let sole = position "100" "5" 1L (Some(bounds "2026-01-11" "2026-01-11"))

                    let empty =
                        FinancialPosition.remove (entry "2026-01-11" "5") None sole |> accepted

                    Expect.equal empty.PaymentCount 0L "Sole removal clears count"
                    Expect.isNone empty.PaymentDates "Sole removal clears both extrema"
                    let all = position "100" "20" 4L (Some(bounds "2026-01-11" "2026-01-15"))

                    let tied =
                        FinancialPosition.remove
                            (entry "2026-01-11" "5")
                            (Some(bounds "2026-01-11" "2026-01-15"))
                            all
                        |> accepted

                    Expect.equal tied.PaymentCount 3L "Tied extremum may remain"

                    let next =
                        FinancialPosition.remove
                            (entry "2026-01-11" "5")
                            (Some(bounds "2026-01-12" "2026-01-15"))
                            all
                        |> accepted

                    Expect.equal
                        (next.PaymentDates |> Option.map _.Earliest)
                        (Some(date "2026-01-12"))
                        "Sole earliest can advance"

                    for selected, remaining in
                        [
                            entry "2026-01-13" "5", Some(bounds "2026-01-12" "2026-01-15")
                            entry "2026-01-11" "5", Some(bounds "2026-01-11" "2026-01-14")
                            entry "2026-01-10" "5", Some(bounds "2026-01-11" "2026-01-15")
                            entry "2026-01-11" "5", None
                        ] do
                        Expect.equal
                            (FinancialPosition.remove selected remaining all)
                            (Error FinancialPositionIssue.InvalidRemovalContext)
                            "Unaffected extrema cannot disappear or move")
        ]
