namespace ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Json

module CaseTotalsCodecs =
    open Codec

    let total =
        boundedText 1 (DecimalTotal.MaximumIntegerDigits + DecimalTotal.MaximumFractionalDigits + 1)
        |> mapResult
            (fun path raw ->
                match DecimalTotal.parse raw with
                | Ok value when DecimalTotal.format value = raw -> Ok value
                | _ ->
                    Error
                        {
                            Path = path
                            Reason = "Expected an unsigned canonical exact decimal total."
                        })
            DecimalTotal.format
        |> named "DecimalTotal"

    let private currency =
        ResponseScalars.currency
        |> mapResult
            (fun path raw ->
                Currency.parse raw
                |> Result.mapError (fun _ ->
                    {
                        Path = path
                        Reason = "Invalid currency."
                    }))
            Currency.format

    let private currencyTotal =
        (record (fun currency amount ->
            {
                CurrencyTotal.Currency = currency
                Amount = amount
            })
        : Fields<CurrencyTotal, _>)
        |> field "currency" _.Currency currency
        |> field "amount" _.Amount total
        |> build
        |> named "CurrencyTotal"

    let private count name vocabulary =
        (record (fun value count ->
            {
                ValueCount.Value = value
                Count = count
            })
        : Fields<ValueCount<_>, _>)
        |> field "value" _.Value (VocabularyCodec.create vocabulary)
        |> field "count" _.Count (integer 0L JsonValue.MaximumInteger)
        |> build
        |> named name

    let private counts =
        (record (fun total lifecycles statuses progress ->
            {
                CaseWorklistCounts.Total = total
                Lifecycles = lifecycles
                Statuses = statuses
                Progress = progress
            })
        : Fields<CaseWorklistCounts, _>)
        |> field "total" _.Total (integer 0L JsonValue.MaximumInteger)
        |> field
            "lifecycle"
            _.Lifecycles
            (list Lifecycle.all.Length (count "LifecycleCount" Lifecycle.vocabulary))
        |> field
            "status"
            _.Statuses
            (list
                (Vocabulary.all CaseStatus.vocabulary).Length
                (count "StatusCount" CaseStatus.vocabulary))
        |> field
            "progress"
            _.Progress
            (list
                (Vocabulary.all CaseProgress.vocabulary).Length
                (count "ProgressCount" CaseProgress.vocabulary))
        |> build
        |> named "CaseWorklistCounts"

    let summary =
        (record (fun counts claimed payable ->
            {
                CaseWorklistSummary.Counts = counts
                Claimed = claimed
                Payable = payable
            })
        : Fields<CaseWorklistSummary, _>)
        |> field "counts" _.Counts counts
        |> field "claimed" _.Claimed (list 17576 currencyTotal)
        |> field "payable" _.Payable (list 17576 currencyTotal)
        |> build
        |> named "CaseWorklistSummary"
