namespace ClaimCore.RecordFormat

open ClaimCore.Domain
open ClaimCore.Json

/// The bounded financial representation is shared by retained cases and machine projections.
module FinancialCodecs =
    open Codec

    let private amount =
        patternedText
            1
            (Amount.MaximumIntegerDigits + Amount.MaximumFractionalDigits + 1)
            ("^(?:" + Amount.Grammar + ")$")
            None
        |> interpret
            (fun path value ->
                Amount.parse value
                |> Result.mapError (fun _ ->
                    {
                        Path = path
                        Reason = "Invalid exact amount."
                    }))
            Amount.format

    let private date =
        patternedText 10 10 ("^(?:" + CalendarDate.Grammar + ")$") (Some "date")
        |> interpret
            (fun path value ->
                CalendarDate.parse value
                |> Result.mapError (fun _ ->
                    {
                        Path = path
                        Reason = "Invalid calendar date."
                    }))
            CalendarDate.format

    let private currency = RecordScalars.scalar Currency.parse Currency.format

    let private money =
        (record Money.create: Fields<Money, _>)
        |> field "amount" Money.amount amount
        |> field "currency" Money.currencyValue currency
        |> build

    let private direction = VocabularyCodec.create PaymentDirection.vocabulary

    let private entryDetails fields =
        fields
        |> field "direction" PaymentEntry.direction direction
        |> field "money" PaymentEntry.money money
        |> field "paymentDate" PaymentEntry.date date
        |> field
            "reference"
            PaymentEntry.reference
            (nullable (RecordScalars.scalar Identifier.parse Identifier.format))
        |> field
            "payee"
            PaymentEntry.payee
            (nullable (RecordScalars.identifier PartyId.create PartyId.value))
        |> buildResult (fun path reason -> { Path = path; Reason = reason })

    let entry =
        (record PaymentEntry.create: Fields<PaymentEntry, _>)
        |> field "paymentId" PaymentEntry.id RecordScalars.operationId
        |> entryDetails
        |> named "PaymentEntry"

    let privateEntry id =
        (record (PaymentEntry.create id): Fields<PaymentEntry, _>) |> entryDetails

    let difference =
        tagged
            "PaymentDifference"
            [
                variant
                    "RECORDED"
                    PaymentDifference.Recorded
                    (function
                    | PaymentDifference.Recorded value -> Some value
                    | _ -> None)
                    (record id |> field "entry" id entry)
                variant
                    "REMOVED"
                    PaymentDifference.Removed
                    (function
                    | PaymentDifference.Removed value -> Some value
                    | _ -> None)
                    (record id |> field "entry" id entry)
            ]

    let private decimalTotal =
        patternedText
            1
            (DecimalTotal.MaximumIntegerDigits + DecimalTotal.MaximumFractionalDigits + 1)
            ("^(?:" + DecimalTotal.Grammar + ")$")
            None
        |> interpret
            (fun path raw ->
                match DecimalTotal.parse raw with
                | Ok value when DecimalTotal.format value = raw -> Ok value
                | _ ->
                    Error
                        {
                            Path = path
                            Reason = "Invalid canonical payment total."
                        })
            DecimalTotal.format

    let private currencyTotals =
        (record (fun currency paid returned ->
            currency,
            {
                CurrencyPaymentTotals.Paid = paid
                Returned = returned
            })
        : Fields<Currency * CurrencyPaymentTotals, _>)
        |> field "currency" fst currency
        |> field "paid" (snd >> _.Paid) decimalTotal
        |> field "returned" (snd >> _.Returned) decimalTotal
        |> build

    let position decision =
        (record (fun decision totals count ->
            let indexed = Map.ofList totals

            if indexed.Count <> List.length totals then
                Error FinancialPositionIssue.InconsistentAggregate
            else
                FinancialPosition.create decision indexed count)
        : Fields<FinancialPosition, _>)
        |> field "decision" _.Decision (nullable decision)
        |> field "totals" (_.CurrencyTotals >> Map.toList) (list 17576 currencyTotals)
        |> field "paymentCount" _.PaymentCount (integer 0L SafeInteger.Maximum)
        |> buildResult (fun path _ ->
            {
                Path = path
                Reason = "Invalid financial aggregate."
            })
        |> named "FinancialPosition"
