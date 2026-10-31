namespace ClaimCore.Postgres

open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

module internal CaseSummaryQuery =
    let private admitted =
        function
        | Ok value -> value
        | Error _ -> raise (RecordCorruptException "Invalid case aggregate.")

    let private total currency amount =
        match currency, amount with
        | None, None -> None
        | Some currency, Some amount ->
            Some
                {
                    CurrencyTotal.Currency = Currency.parse currency |> admitted
                    Amount = DecimalTotal.parse amount |> admitted
                }
        | _ -> raise (RecordCorruptException "Incomplete aggregate amount and currency.")

    let private group row =
        let lifecycle =
            DurableNames.tryLifecycle (DatabaseSession.value<string> 0 row)
            |> Option.defaultWith (fun () ->
                raise (RecordCorruptException "Invalid aggregate lifecycle."))

        let status =
            DatabaseSession.optional<string> 1 row
            |> Option.map (fun value ->
                DurableNames.tryStatus value
                |> Option.defaultWith (fun () ->
                    raise (RecordCorruptException "Invalid aggregate status.")))

        let progress =
            DatabaseSession.optional<string> 2 row
            |> Option.map (fun value ->
                DurableNames.tryProgress value
                |> Option.defaultWith (fun () ->
                    raise (RecordCorruptException "Invalid aggregate progress.")))

        {
            CaseCountGroup.Lifecycle = lifecycle
            Status = status
            Progress = progress
            Count = DatabaseSession.value<int64> 3 row
            Claimed =
                total
                    (DatabaseSession.optional<string> 4 row)
                    (DatabaseSession.optional<string> 5 row)
            Payable =
                total
                    (DatabaseSession.optional<string> 6 row)
                    (DatabaseSession.optional<string> 7 row)
        }

    let read connection selection token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    ($"SELECT c.state,c.status,c.progress,count(*),c.claimed_currency,sum(c.claimed_amount)::text,c.payable_currency,sum(c.payable_amount)::text FROM claimcore.cases c WHERE {CaseSelectionSql.predicate} GROUP BY c.state,c.status,c.progress,c.claimed_currency,c.payable_currency")
                    (CaseSelectionSql.parameters selection)
                    token

            return
                rows
                |> List.map group
                |> CaseWorklistSummary.fromGroups
                |> Result.defaultWith (fun _ ->
                    raise (RecordCorruptException "Invalid case totals or counts."))
        }
