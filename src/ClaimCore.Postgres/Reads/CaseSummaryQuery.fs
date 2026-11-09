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
            (Vocabulary.tryParse Lifecycle.vocabulary) (DatabaseSession.value<string> 0 row)
            |> Option.defaultWith (fun () ->
                raise (RecordCorruptException "Invalid aggregate lifecycle."))

        let status =
            DatabaseSession.optional<string> 1 row
            |> Option.map (fun value ->
                (Vocabulary.tryParse CaseStatus.vocabulary) value
                |> Option.defaultWith (fun () ->
                    raise (RecordCorruptException "Invalid aggregate status.")))

        let progress =
            DatabaseSession.optional<string> 2 row
            |> Option.map (fun value ->
                (Vocabulary.tryParse CaseProgress.vocabulary) value
                |> Option.defaultWith (fun () ->
                    raise (RecordCorruptException "Invalid aggregate progress.")))

        {
            CaseCountGroup.Responsibility =
                DatabaseSession.optional<string> 8 row
                |> Option.map (fun value ->
                    Vocabulary.tryParse CaseAssignment.vocabulary value
                    |> Option.defaultWith (fun () ->
                        raise (RecordCorruptException "Invalid responsibility aggregate.")))
            Due =
                Vocabulary.tryParse CaseDue.vocabulary (DatabaseSession.value<string> 9 row)
                |> Option.defaultWith (fun () ->
                    raise (RecordCorruptException "Invalid due aggregate."))
            Lifecycle = lifecycle
            Status = status
            Progress = progress
            Count = DatabaseSession.value<int64> 3 row
            Claimed =
                total
                    (DatabaseSession.optional<string> 4 row)
                    (DatabaseSession.optional<string> 5 row)
            Paid =
                total
                    (DatabaseSession.optional<string> 6 row)
                    (DatabaseSession.optional<string> 10 row)
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
                    ($"SELECT c.state,c.status,c.progress,count(*),c.claimed_currency,sum(c.claimed_amount)::text,c.payable_currency,sum(c.payable_amount)::text,({CaseSelectionSql.responsibility}),({CaseSelectionSql.due}),sum(c.paid_amount)::text FROM claimcore.cases c WHERE {CaseSelectionSql.predicate selection} GROUP BY c.state,c.status,c.progress,c.claimed_currency,c.payable_currency,9,10")
                    (CaseSelectionSql.parameters selection)
                    token

            return
                rows
                |> List.map group
                |> CaseWorklistSummary.fromGroups selection.DateBasis
                |> Result.defaultWith (fun _ ->
                    raise (RecordCorruptException "Invalid case totals or counts."))
        }
