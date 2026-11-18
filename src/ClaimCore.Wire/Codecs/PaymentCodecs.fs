namespace ClaimCore.Wire

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

/// Accepted entries and their financial context keep one wire shape in reads and history.
module PaymentCodecs =
    open Codec
    open ResponseScalars

    let entry = ClaimCore.RecordFormat.FinancialCodecs.entry
    let private difference = ClaimCore.RecordFormat.FinancialCodecs.difference

    let totals =
        (record (fun payable netPaid ->
            {
                PaymentTotals.Payable = payable
                NetPaid = netPaid
            })
        : Fields<PaymentTotals, _>)
        |> field "payable" _.Payable money
        |> field "netPaid" _.NetPaid (decimalTotal true)
        |> build
        |> named "PaymentTotals"

    let payee =
        (record (fun id name removed ->
            {
                PaymentPayee.Id = id
                Name = name
                Removed = removed
            })
        : Fields<PaymentPayee, _>)
        |> field
            "id"
            _.Id
            (uuid
             |> interpret
                 (fun path raw ->
                     PartyId.create raw
                     |> Option.map Ok
                     |> Option.defaultValue (
                         Error
                             {
                                 Path = path
                                 Reason = "Invalid party identity."
                             }
                     ))
                 PartyId.value)
        |> field "name" _.Name (nullable partyName)
        |> field "removed" _.Removed boolean
        |> build
        |> named "PaymentPayee"

    let changes =
        (record PaymentCollectionDifference.create: Fields<PaymentCollectionDifference, _>)
        |> field "entries" _.Entries (boundedList 1 1 difference)
        |> field "before" _.Before (nullable totals)
        |> field "after" _.After (nullable totals)
        |> buildResult (fun path reason -> { Path = path; Reason = reason })
        |> named "PaymentCollectionDifference"
