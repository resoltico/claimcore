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

    let private month =
        boundedText 7 7
        |> interpret
            (fun path raw ->
                CaseMonth.parse raw
                |> Result.mapError (fun _ ->
                    {
                        Path = path
                        Reason = "Expected a calendar month."
                    }))
            CaseMonth.format

    let private key =
        tagged
            "CaseGroupKey"
            [
                variant
                    "MONTH"
                    CaseGroupKey.Month
                    (function
                    | CaseGroupKey.Month value -> Some value
                    | _ -> None)
                    (record id |> field "value" id month)
                variant
                    "OUTCOME"
                    CaseGroupKey.Outcome
                    (function
                    | CaseGroupKey.Outcome value -> Some value
                    | _ -> None)
                    (record id
                     |> field "value" id (VocabularyCodec.create ClosureOutcome.vocabulary))
                variant
                    "RESPONSIBLE"
                    CaseGroupKey.Responsible
                    (function
                    | CaseGroupKey.Responsible value -> Some value
                    | _ -> None)
                    (record id |> field "value" id ActorCodecs.actorRef)
                variant
                    "DAYS_OPEN"
                    CaseGroupKey.DaysOpen
                    (function
                    | CaseGroupKey.DaysOpen value -> Some value
                    | _ -> None)
                    (record id |> field "value" id (VocabularyCodec.create DaysOpenBand.vocabulary))
                variant
                    "MISSING"
                    CaseGroupKey.Missing
                    (function
                    | CaseGroupKey.Missing value -> Some value
                    | _ -> None)
                    (record id
                     |> field "value" id (VocabularyCodec.create MissingCaseGroup.vocabulary))
            ]

    let private group =
        (record (fun key count claimed payable netPaid ->
            {
                CaseMoneyGroup.Group = key
                Count = count
                Claimed = claimed
                Payable = payable
                NetPaid = netPaid
            })
        : Fields<CaseMoneyGroup, _>)
        |> field "group" _.Group key
        |> field "count" _.Count (integer 0L JsonValue.MaximumInteger)
        |> field "claimed" _.Claimed (list 17576 (currencyTotal false))
        |> field "payable" _.Payable (list 17576 (currencyTotal false))
        |> field "netPaid" _.NetPaid (list 17576 (currencyTotal true))
        |> build
        |> named "CaseMoneyGroup"

    let summary =
        (record (fun counts claimed payable netPaid basis groupBy groups ->
            {
                CaseWorklistSummary.Basis = basis
                Counts = counts
                Claimed = claimed
                Payable = payable
                NetPaid = netPaid
                GroupBy = groupBy
                Groups = defaultArg groups []
            })
        : Fields<CaseWorklistSummary, _>)
        |> field "counts" _.Counts counts
        |> field "claimed" _.Claimed (list 17576 (currencyTotal false))
        |> field "payable" _.Payable (list 17576 (currencyTotal false))
        |> field "netPaid" _.NetPaid (list 17576 (currencyTotal true))
        |> field "basis" _.Basis ResponseScalars.workDateBasis
        |> optionalField "groupBy" _.GroupBy (VocabularyCodec.create CaseGrouping.vocabulary)
        |> optionalField
            "groups"
            (fun summary -> if summary.GroupBy.IsSome then Some summary.Groups else None)
            (list System.Int32.MaxValue group)
        |> build
        |> named "CaseWorklistSummary"
