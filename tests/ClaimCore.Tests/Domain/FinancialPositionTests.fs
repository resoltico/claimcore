module ClaimCore.Tests.FinancialPositionTests

open Expecto
open ClaimCore.Domain
open ClaimCore.TestSupport

let private accepted result =
    result |> Result.defaultWith (fun error -> failtestf "%A" error)

let private eur = Currency.parse "EUR" |> accepted
let private usd = Currency.parse "USD" |> accepted

let private total value =
    DecimalTotal.parseSigned value |> accepted

let private date = DomainInputs.date

let private decision amount currency =
    DomainInputs.decisionFrom
        [
            BusinessField.PaymentDecisionDate, "2026-11-01"
            BusinessField.PayableAmount, amount
            BusinessField.PayableCurrency, currency
        ]
    |> DomainInputs.admittedDecision

let private entry ordinal direction dateText amount currency =
    let identity =
        System.Guid.Parse(sprintf "10000000-0000-7000-8000-%012d" ordinal)
        |> OperationId.create
        |> Option.get

    PaymentEntry.create
        identity
        direction
        (DomainInputs.money amount currency)
        (date dateText)
        None
        None
    |> accepted

let private add payment position =
    FinancialPosition.add payment position |> accepted

let private decide amount currency position =
    FinancialPosition.withDecision (Some(decision amount currency)) position

[<Tests>]
let tests =
    testList
        "private financial position"
        [
            testCase "overpayment, returns and zero payable classify exact net" (fun () ->
                let original = FinancialPosition.undecided |> decide "100" "EUR"
                let paid = original |> add (entry 1 PaymentDirection.Paid "2026-11-03" "120" "EUR")
                Expect.equal (FinancialPosition.netIn eur paid) (total "120") "Exact overpayment"
                Expect.equal (CaseProgress.ofFinancial paid) CaseProgress.Overpaid "Overpaid"
                Expect.isTrue (FinancialPosition.needsReview paid) "Overpayment needs review"

                let returned =
                    paid |> add (entry 2 PaymentDirection.Returned "2026-11-06" "20" "EUR")

                Expect.equal
                    (FinancialPosition.netIn eur returned)
                    (total "100")
                    "Return offsets payment"

                Expect.equal (CaseProgress.ofFinancial returned) CaseProgress.Paid "Paid"
                Expect.equal returned.PaymentCount 2L "Both entries remain current"

                Expect.isFalse
                    (FinancialPosition.needsReview returned)
                    "Exact payable needs no review"

                let zero = FinancialPosition.undecided |> decide "0" "EUR"

                Expect.equal
                    (CaseProgress.ofFinancial zero)
                    CaseProgress.Paid
                    "Zero payable is paid")
            testCase
                "payment buckets are independent of decision presence, date and currency"
                (fun () ->
                    let dollars = entry 1 PaymentDirection.Paid "2026-10-01" "50" "USD"
                    let initial = FinancialPosition.undecided |> add dollars

                    Expect.equal
                        (CaseProgress.ofFinancial initial)
                        CaseProgress.Undecided
                        "No decision required"

                    Expect.equal
                        (FinancialPosition.netIn usd initial)
                        (total "50")
                        "The entry keeps USD"

                    let revised = initial |> decide "100" "EUR"

                    Expect.equal
                        (CaseProgress.ofFinancial revised)
                        CaseProgress.Decided
                        "No net EUR payment"

                    Expect.equal
                        (FinancialPosition.netIn usd revised)
                        (total "50")
                        "Currency change keeps entries"

                    let again = revised |> FinancialPosition.withDecision None
                    Expect.equal again initial "Withdrawal preserves the finance buckets")
            testCase
                "negative net in any currency requires review and removing an entry subtracts its bucket"
                (fun () ->
                    let returned = entry 1 PaymentDirection.Returned "2026-11-12" "30" "EUR"
                    let paid = entry 2 PaymentDirection.Paid "2026-11-12" "50" "USD"
                    let both = FinancialPosition.undecided |> add returned |> add paid

                    Expect.equal
                        (FinancialPosition.netIn eur both)
                        (total "-30")
                        "A net return is signed"

                    Expect.isTrue
                        (FinancialPosition.needsReview both)
                        "A negative bucket requires review"

                    let remaining = FinancialPosition.remove returned both |> accepted

                    Expect.equal
                        (Map.keys remaining.CurrencyTotals |> Seq.toList)
                        [ usd ]
                        "Only USD remains"

                    Expect.equal
                        (FinancialPosition.netIn usd remaining)
                        (total "50")
                        "Other buckets stay exact"

                    Expect.equal remaining.PaymentCount 1L "Only the removed entry leaves")
            testCase
                "signed net retains fractional precision and totals reject a forty-first digit"
                (fun () ->
                    Expect.equal
                        (DecimalTotal.format (total "-0.0001"))
                        "-0.0001"
                        "Fractional sign"

                    let maximum = total "9999999999999999999999999999999999999999.9999"

                    let buckets =
                        {
                            CurrencyPaymentTotals.Paid = maximum
                            Returned = DecimalTotal.zero
                        }

                    let position =
                        FinancialPosition.create None (Map.ofList [ eur, buckets ]) 1L |> accepted

                    let added = entry 1 PaymentDirection.Paid "2026-11-01" "0.0001" "EUR"

                    Expect.equal
                        (FinancialPosition.add added position)
                        (Error FinancialPositionIssue.TotalOutOfRange)
                        "An out-of-range total is refused"

                    Expect.equal
                        (FinancialPosition.netIn eur position)
                        maximum
                        "Before-state is exact")
        ]
