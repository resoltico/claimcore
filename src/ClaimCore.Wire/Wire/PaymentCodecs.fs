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
             |> mapResult
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
        (record (fun payable paid remaining ->
            {
                PaymentTotals.Payable = payable
                Paid = paid
                Remaining = remaining
            })
        : Fields<PaymentTotals, _>)
        |> field "payable" _.Payable money
        |> field "paid" _.Paid money
        |> field "remaining" _.Remaining money
        |> build
        |> mapResult
            (fun path totals ->
                if
                    Money.currency totals.Payable = Money.currency totals.Paid
                    && Money.currency totals.Payable = Money.currency totals.Remaining
                    && Money.value totals.Paid + Money.value totals.Remaining =
                        Money.value totals.Payable
                then
                    Ok totals
                else
                    Error
                        {
                            Path = path
                            Reason =
                                "Payment totals require one currency and paid plus remaining equal to payable."
                        })
            id
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
        (record (fun entries before after ->
            {
                PaymentCollectionDifference.Entries = entries
                Before = before
                After = after
            })
        : Fields<PaymentCollectionDifference, _>)
        |> field "entries" _.Entries (boundedList 1 PaymentProgress.MaximumPayments difference)
        |> field "before" _.Before (nullable totals)
        |> field "after" _.After (nullable totals)
        |> build
        |> mapResult
            (fun path changes ->
                if
                    changes.Entries
                    |> List.forall (function
                        | PaymentDifference.Recorded _ -> changes.After.IsSome
                        | PaymentDifference.Removed _ -> changes.Before.IsSome)
                then
                    Ok changes
                else
                    Error
                        {
                            Path = path
                            Reason = "Payment changes require their financial context."
                        })
            id
        |> named "PaymentCollectionDifference"
