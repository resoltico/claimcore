namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type FinancialPositionIssue =
    | InconsistentAggregate
    | CountOutOfRange
    | TotalOutOfRange
    | InvalidRemovalContext

type CurrencyPaymentTotals =
    {
        Paid: DecimalTotal
        Returned: DecimalTotal
    }

module CurrencyPaymentTotals =
    let zero =
        {
            Paid = DecimalTotal.zero
            Returned = DecimalTotal.zero
        }

    let net total =
        DecimalTotal.sum [ total.Paid; DecimalTotal.negate total.Returned ]
        |> Result.defaultWith (fun _ ->
            invalidOp "Non-negative payment buckets have a bounded difference.")

type PaymentTotals =
    {
        Payable: Money
        NetPaid: DecimalTotal
    }

/// Current payments are retained as exact, non-negative buckets by currency and direction.
type FinancialPosition =
    private
        {
            DecisionValue: Decision option
            TotalsValue: Map<Currency, CurrencyPaymentTotals>
            CountValue: int64
        }

    member this.Decision = this.DecisionValue
    member this.CurrencyTotals = this.TotalsValue
    member this.PaymentCount = this.CountValue

module FinancialPosition =
    let create decision totals count =
        if count < 0L || count > SafeInteger.Maximum then
            Error FinancialPositionIssue.CountOutOfRange
        elif
            (Map.isEmpty totals <> (count = 0L))
            || totals
               |> Map.exists (fun _ totals ->
                   totals.Paid < DecimalTotal.zero
                   || totals.Returned < DecimalTotal.zero
                   || totals = CurrencyPaymentTotals.zero)
        then
            Error FinancialPositionIssue.InconsistentAggregate
        else
            Ok
                {
                    DecisionValue = decision
                    TotalsValue = totals
                    CountValue = count
                }

    let undecided =
        {
            DecisionValue = None
            TotalsValue = Map.empty
            CountValue = 0L
        }

    let withDecision decision position =
        { position with
            DecisionValue = decision
        }

    let netIn currency (position: FinancialPosition) =
        position.CurrencyTotals
        |> Map.tryFind currency
        |> Option.map CurrencyPaymentTotals.net
        |> Option.defaultValue DecimalTotal.zero

    let totals (position: FinancialPosition) =
        position.Decision
        |> Option.map (fun decision ->
            let payable = Decision.payable decision

            {
                Payable = payable
                NetPaid = netIn (Money.currencyValue payable) position
            })

    let needsReview (position: FinancialPosition) =
        (totals position
         |> Option.exists (fun totals ->
             totals.NetPaid > DecimalTotal.ofAmount (Money.amount totals.Payable)))
        || position.CurrencyTotals
           |> Map.exists (fun _ totals -> CurrencyPaymentTotals.net totals < DecimalTotal.zero)

    let private adjust combine issue direction money totals =
        let currency = Money.currencyValue money

        let original =
            Map.tryFind currency totals |> Option.defaultValue CurrencyPaymentTotals.zero

        let amount = DecimalTotal.ofAmount (Money.amount money) |> combine

        let previous, update =
            match direction with
            | PaymentDirection.Paid -> original.Paid, fun value -> { original with Paid = value }
            | PaymentDirection.Returned ->
                original.Returned, fun value -> { original with Returned = value }

        match DecimalTotal.sum [ previous; amount ] with
        | Ok value when value >= DecimalTotal.zero ->
            let next = update value

            if next = CurrencyPaymentTotals.zero then
                Ok(Map.remove currency totals)
            else
                Ok(Map.add currency next totals)
        | _ -> Error issue

    let add entry (position: FinancialPosition) =
        if position.PaymentCount = SafeInteger.Maximum then
            Error FinancialPositionIssue.CountOutOfRange
        else
            adjust
                id
                FinancialPositionIssue.TotalOutOfRange
                (PaymentEntry.direction entry)
                (PaymentEntry.money entry)
                position.CurrencyTotals
            |> Result.bind (fun totals ->
                create position.Decision totals (position.PaymentCount + 1L))

    let remove entry (position: FinancialPosition) =
        adjust
            DecimalTotal.negate
            FinancialPositionIssue.InvalidRemovalContext
            (PaymentEntry.direction entry)
            (PaymentEntry.money entry)
            position.CurrencyTotals
        |> Result.bind (fun totals -> create position.Decision totals (position.PaymentCount - 1L))

    let rebuild entries =
        (Ok undecided, entries)
        ||> Seq.fold (fun state entry -> state |> Result.bind (add entry))
