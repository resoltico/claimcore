namespace ClaimCore.Wire

open ClaimCore.Domain
open ClaimCore.Json

/// Accepted entries and their financial context keep one wire shape in reads and history.
module PaymentCodecs =
    open Codec
    open ResponseScalars

    let entry = ClaimCore.RecordFormat.FinancialCodecs.entry
    let private difference = ClaimCore.RecordFormat.FinancialCodecs.difference

    let private totals =
        (record PaymentTotals.create: Fields<PaymentTotals, _>)
        |> field "payable" _.Payable money
        |> field "paid" _.Paid money
        |> field "remaining" _.Remaining money
        |> buildResult (fun path reason -> { Path = path; Reason = reason })
        |> named "PaymentTotals"

    let changes =
        (record PaymentCollectionDifference.create: Fields<PaymentCollectionDifference, _>)
        |> field "entries" _.Entries (boundedList 1 1 difference)
        |> field "before" _.Before (nullable totals)
        |> field "after" _.After (nullable totals)
        |> buildResult (fun path reason -> { Path = path; Reason = reason })
        |> named "PaymentCollectionDifference"
