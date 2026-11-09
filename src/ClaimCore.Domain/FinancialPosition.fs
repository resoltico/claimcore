namespace ClaimCore.Domain

open System

type PaymentTotals =
    {
        Payable: Money
        Paid: Money
        Remaining: Money
    }

module PaymentTotals =
    let create payable paid remaining =
        if
            Money.currency payable = Money.currency paid
            && Money.currency payable = Money.currency remaining
            && Money.value paid + Money.value remaining = Money.value payable
        then
            Ok
                {
                    Payable = payable
                    Paid = paid
                    Remaining = remaining
                }
        else
            Error "Payment totals require one currency and paid plus remaining equal to payable."

/// Bounded date facts belong to an exact active-entry set, not an arbitrary read page.
type PaymentDateBounds =
    private
        {
            EarliestValue: CalendarDate
            LatestValue: CalendarDate
        }

    member this.Earliest = this.EarliestValue
    member this.Latest = this.LatestValue

module PaymentDateBounds =
    let create (earliest: CalendarDate) (latest: CalendarDate) =
        if earliest <= latest then
            Some
                {
                    EarliestValue = earliest
                    LatestValue = latest
                }
        else
            None

[<RequireQualifiedAccess>]
type FinancialPositionIssue =
    | InconsistentAggregate
    | CountOutOfRange
    | DecisionRequired
    | DecisionHasPayments
    | CurrencyMismatch
    | ExceedsPayable
    | PaymentBeforeDecision
    | FuturePayment
    | InvalidRemovalContext

/// Only accepted facts are stored: classification and remaining money are derived.
type FinancialPosition =
    private
        {
            DecisionValue: Decision option
            PaidValue: Amount
            CountValue: int64
            DateValues: PaymentDateBounds option
        }

    member this.Decision = this.DecisionValue
    member this.PaidAmount = this.PaidValue
    member this.PaymentCount = this.CountValue
    member this.PaymentDates = this.DateValues

module FinancialPosition =
    let private amount (value: decimal) =
        value.ToString("0.####", Globalization.CultureInfo.InvariantCulture)
        |> Amount.parse
        |> Result.defaultWith (fun _ ->
            invalidOp "A bounded financial amount must be representable.")

    let private zero = amount 0M
    // Every active entry is positive and has the Amount precision, so its minimum contribution is exact.
    let private quantum = 1M / pown 10M Amount.MaximumFractionalDigits

    let create
        (decision: Decision option)
        (paid: Amount)
        (count: int64)
        (dates: PaymentDateBounds option)
        =
        let paidValue = Amount.value paid

        let consistent =
            if count = 0L then
                paidValue = 0M && Option.isNone dates
            else
                paidValue >= decimal count * quantum
                && (dates
                    |> Option.exists (fun bounds -> count <> 1L || bounds.Earliest = bounds.Latest))

        if count < 0L || count > SafeInteger.Maximum then
            Error FinancialPositionIssue.CountOutOfRange
        elif not consistent then
            Error FinancialPositionIssue.InconsistentAggregate
        else
            match decision with
            | None when count <> 0L || paidValue <> 0M ->
                Error FinancialPositionIssue.DecisionRequired
            | Some value when paidValue > Money.value (Decision.payable value) ->
                Error FinancialPositionIssue.ExceedsPayable
            | Some value when
                dates |> Option.exists (fun dates -> dates.Earliest < Decision.date value)
                ->
                Error FinancialPositionIssue.PaymentBeforeDecision
            | _ ->
                Ok
                    {
                        DecisionValue = decision
                        PaidValue = paid
                        CountValue = count
                        DateValues = dates
                    }

    let undecided =
        {
            DecisionValue = None
            PaidValue = zero
            CountValue = 0L
            DateValues = None
        }

    let isPaid (position: FinancialPosition) =
        position.Decision
        |> Option.exists (fun decision ->
            Amount.value position.PaidAmount = Money.value (Decision.payable decision))

    let totals (position: FinancialPosition) =
        position.Decision
        |> Option.map (fun decision ->
            let payable = Decision.payable decision
            let currency = Money.currencyValue payable

            PaymentTotals.create
                payable
                (Money.create position.PaidAmount currency)
                (Money.create
                    (amount (Money.value payable - Amount.value position.PaidAmount))
                    currency)
            |> Result.defaultWith invalidOp)

    let withDecision decision (position: FinancialPosition) =
        match position.Decision, decision with
        | _, None when position.PaymentCount > 0L ->
            Error FinancialPositionIssue.DecisionHasPayments
        | Some before, Some after when
            position.PaymentCount > 0L
            && Money.currency (Decision.payable before)
               <> Money.currency (Decision.payable after)
            ->
            Error FinancialPositionIssue.CurrencyMismatch
        | _ -> create decision position.PaidAmount position.PaymentCount position.PaymentDates

    let record today (payment: Payment) (position: FinancialPosition) =
        match position.Decision with
        | None -> Error FinancialPositionIssue.DecisionRequired
        | Some decision ->
            let paid = Payment.paid payment
            let date = Payment.date payment
            let value = Money.value paid

            if value <= 0M then
                Error FinancialPositionIssue.InconsistentAggregate
            elif Money.currency paid <> Money.currency (Decision.payable decision) then
                Error FinancialPositionIssue.CurrencyMismatch
            elif CalendarDate.value date > today then
                Error FinancialPositionIssue.FuturePayment
            elif position.PaymentCount = SafeInteger.Maximum then
                Error FinancialPositionIssue.CountOutOfRange
            elif
                value > Money.value (Decision.payable decision) - Amount.value position.PaidAmount
            then
                Error FinancialPositionIssue.ExceedsPayable
            else
                let earliest, latest =
                    match position.PaymentDates with
                    | None -> date, date
                    | Some dates -> min date dates.Earliest, max date dates.Latest

                create
                    position.Decision
                    (amount (Amount.value position.PaidAmount + value))
                    (position.PaymentCount + 1L)
                    (PaymentDateBounds.create earliest latest)

    /// Remaining extrema must be supplied by the store from the same locked active-entry set.
    /// This validates their relationship to the selected entry; replay independently derives them.
    let remove
        (selected: PaymentEntry)
        (remainingDates: PaymentDateBounds option)
        (position: FinancialPosition)
        =
        let date = PaymentEntry.date selected

        let paid =
            Amount.value position.PaidAmount - Amount.value (PaymentEntry.amount selected)

        let validBounds =
            match position.PaymentDates, remainingDates with
            | Some before, None ->
                position.PaymentCount = 1L && date = before.Earliest && date = before.Latest
            | Some before, Some after ->
                position.PaymentCount > 1L
                && date >= before.Earliest
                && date <= before.Latest
                && after.Earliest >= before.Earliest
                && after.Latest <= before.Latest
                && (date = before.Earliest || after.Earliest = before.Earliest)
                && (date = before.Latest || after.Latest = before.Latest)
            | _ -> false

        if not validBounds || paid < 0M then
            Error FinancialPositionIssue.InvalidRemovalContext
        else
            create position.Decision (amount paid) (position.PaymentCount - 1L) remainingDates
            |> Result.mapError (fun _ -> FinancialPositionIssue.InvalidRemovalContext)
