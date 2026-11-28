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

    let entryDifference = difference

    let private balance =
        (record BalanceDifference.create: Fields<BalanceDifference, _>)
        |> field "currency" _.Currency currencyCode
        |> field "net" _.Net (ValueChangeCodecs.change "NetBalanceValueChange" (decimalTotal true))
        |> buildResult (fun path reason -> { Path = path; Reason = reason })
        |> named "BalanceDifference"

    let balances minimum =
        boundedList minimum BalanceDifference.MaximumPerEffect balance
        |> interpret
            (fun path items ->
                if
                    items |> List.pairwise |> List.forall (fun (a, b) -> a.Currency < b.Currency)
                then
                    Ok items
                else
                    Error
                        {
                            Path = path
                            Reason = "Balance currencies must be in strict order."
                        })
            id
