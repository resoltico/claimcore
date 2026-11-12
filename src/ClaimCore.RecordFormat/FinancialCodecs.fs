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

    let entry =
        let identity =
            uuid
            |> interpret
                (fun path value ->
                    OperationId.create value
                    |> Option.map Ok
                    |> Option.defaultValue (
                        Error
                            {
                                Path = path
                                Reason = "A payment identity is its accepting operation UUID."
                            }
                    ))
                OperationId.value

        (record PaymentEntry.create: Fields<PaymentEntry, _>)
        |> field "paymentId" PaymentEntry.id identity
        |> field "paymentDate" PaymentEntry.date date
        |> field "paidAmount" PaymentEntry.amount amount
        |> buildResult (fun path reason -> { Path = path; Reason = reason })
        |> named "PaymentEntry"

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

    let position decision =
        (record (fun decision paid count first last ->
            let dates =
                match first, last with
                | None, None -> Ok None
                | Some first, Some last ->
                    PaymentDateBounds.create first last
                    |> Option.map (Some >> Ok)
                    |> Option.defaultValue (Error FinancialPositionIssue.InconsistentAggregate)
                | _ -> Error FinancialPositionIssue.InconsistentAggregate

            dates |> Result.bind (FinancialPosition.create decision paid count))
        : Fields<FinancialPosition, _>)
        |> field "decision" _.Decision (nullable decision)
        |> field "paidAmount" _.PaidAmount amount
        |> field "paymentCount" _.PaymentCount (integer 0L SafeInteger.Maximum)
        |> field
            "firstPaymentDate"
            (fun value -> value.PaymentDates |> Option.map _.Earliest)
            (nullable date)
        |> field
            "lastPaymentDate"
            (fun value -> value.PaymentDates |> Option.map _.Latest)
            (nullable date)
        |> buildResult (fun path _ ->
            {
                Path = path
                Reason = "Financial decision, paid total, count and date boundaries must agree."
            })
        |> named "FinancialPosition"
