namespace ClaimCore.Wire

open ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

module CaseTotalsCodecs =
    open Codec

    let total = ResponseScalars.decimalTotal false
    let signedTotal = ResponseScalars.decimalTotal true

    let private currency =
        ResponseScalars.currency
        |> interpret
            (fun path raw ->
                Currency.parse raw
                |> Result.mapError (fun _ ->
                    {
                        Path = path
                        Reason = "Invalid currency."
                    }))
            Currency.format

    let private currencyTotal signed =
        (record (fun currency amount cases ->
            {
                CurrencyTotal.Currency = currency
                Amount = amount
                Cases = cases
            })
        : Fields<CurrencyTotal, _>)
        |> field "currency" _.Currency currency
        |> field "amount" _.Amount (if signed then signedTotal else total)
        |> field "cases" _.Cases (integer 0L SafeInteger.Maximum)
        |> build
        |> named (if signed then "CurrencyNetTotal" else "CurrencyTotal")

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
        (record (fun total lifecycles statuses progress assignment due ->
            {
                CaseWorklistCounts.Assignment = assignment
                Due = due
                Total = total
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
        |> field
            "assignment"
            _.Assignment
            (list
                (Vocabulary.all CaseAssignment.vocabulary).Length
                (count "AssignmentCount" CaseAssignment.vocabulary))
        |> field
            "due"
            _.Due
            (list (Vocabulary.all CaseDue.vocabulary).Length (count "DueCount" CaseDue.vocabulary))
        |> build
        |> named "CaseWorklistCounts"

    let summary =
        (record (fun counts claimed payable netPaid basis ->
            {
                CaseWorklistSummary.Basis = basis
                Counts = counts
                Claimed = claimed
                Payable = payable
                NetPaid = netPaid
            })
        : Fields<CaseWorklistSummary, _>)
        |> field "counts" _.Counts counts
        |> field "claimed" _.Claimed (list 17576 (currencyTotal false))
        |> field "payable" _.Payable (list 17576 (currencyTotal false))
        |> field "netPaid" _.NetPaid (list 17576 (currencyTotal true))
        |> field "basis" _.Basis ResponseScalars.workDateBasis
        |> build
        |> named "CaseWorklistSummary"
