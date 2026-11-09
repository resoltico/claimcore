namespace ClaimCore.Wire

open ClaimCore.Domain
open ClaimCore.Json

/// Accepted entries and their financial context keep one wire shape in reads and history.
module PaymentCodecs =
    open Codec
    open ResponseScalars

    let entry =
        (record (fun id date amount -> PaymentEntry.create id date amount): Fields<PaymentEntry, _>)
        |> field "paymentId" PaymentEntry.id operationId
        |> field "paymentDate" PaymentEntry.date calendarDate
        |> field
            "paidAmount"
            PaymentEntry.amount
            (amount
             |> interpret
                 (fun path raw ->
                     Amount.parse raw
                     |> Result.mapError (fun _ ->
                         {
                             Path = path
                             Reason = "Invalid payment amount."
                         }))
                 Amount.format)
        |> buildResult (fun path reason -> { Path = path; Reason = reason })
        |> named "PaymentEntry"

    let private totals =
        (record PaymentTotals.create: Fields<PaymentTotals, _>)
        |> field "payable" _.Payable money
        |> field "paid" _.Paid money
        |> field "remaining" _.Remaining money
        |> buildResult (fun path reason -> { Path = path; Reason = reason })
        |> named "PaymentTotals"

    let private difference =
        let variant kind inject project =
            Codec.variant kind inject project (record id |> field "entry" id entry)

        tagged
            "PaymentDifference"
            [
                variant "RECORDED" PaymentDifference.Recorded (function
                    | PaymentDifference.Recorded entry -> Some entry
                    | _ -> None)
                variant "REMOVED" PaymentDifference.Removed (function
                    | PaymentDifference.Removed entry -> Some entry
                    | _ -> None)
            ]

    let changes =
        (record PaymentCollectionDifference.create: Fields<PaymentCollectionDifference, _>)
        |> field "entries" _.Entries (boundedList 1 PaymentProgress.MaximumPayments difference)
        |> field "before" _.Before (nullable totals)
        |> field "after" _.After (nullable totals)
        |> buildResult (fun path reason -> { Path = path; Reason = reason })
        |> named "PaymentCollectionDifference"
