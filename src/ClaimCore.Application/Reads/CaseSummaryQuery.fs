namespace ClaimCore.Application.Reads

open ClaimCore.Application.Storage

open ClaimCore.Application.Execution

open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

module internal CaseSummaryQuery =
    let private admitted =
        function
        | Ok value -> value
        | Error _ -> raise (RecordCorruptException "Invalid case aggregate.")

    let private total signed count currency amount =
        match currency, amount with
        | None, None -> None
        | Some currency, Some amount ->
            Some
                {
                    CurrencyTotal.Currency = Currency.parse currency |> admitted
                    Amount =
                        (if signed then
                             DecimalTotal.parseSigned
                         else
                             DecimalTotal.parse)
                            amount
                        |> admitted
                    Cases = count
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

        let count = DatabaseSession.value<int64> 3 row

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
            Count = count
            Claimed =
                total
                    false
                    count
                    (DatabaseSession.optional<string> 4 row)
                    (DatabaseSession.optional<string> 5 row)
            Payable =
                total
                    false
                    count
                    (DatabaseSession.optional<string> 6 row)
                    (DatabaseSession.optional<string> 7 row)
        }

    let private grouping =
        function
        | None -> "NULL::text", [||]
        | Some CaseGrouping.RegisteredMonth -> "to_char(c.registered_on,'YYYY-MM')", [||]
        | Some CaseGrouping.ReceivedMonth -> "to_char(c.received_on,'YYYY-MM')", [||]
        | Some CaseGrouping.ClosedMonth -> "to_char(c.closed_on,'YYYY-MM')", [||]
        | Some CaseGrouping.Outcome -> "c.outcome", [||]
        | Some CaseGrouping.Responsible -> "c.responsible_actor::text", [||]
        | Some CaseGrouping.DaysOpen ->
            let bounds = DaysOpenBand.bounds |> List.map snd |> List.toArray
            let parameter = CaseSelectionSql.suffix 1

            let cases =
                DaysOpenBand.bounds
                |> List.mapi (fun index _ ->
                    $"WHEN (COALESCE(c.closed_on,$6::date)-c.registered_on) <= ({parameter}::integer[])[{index + 1}] THEN '{index}'")
                |> String.concat " "

            $"CASE {cases} ELSE '{bounds.Length}' END", [| box bounds |]

    let private invalid message = raise (RecordCorruptException message)

    let private actorId (value: string) =
        match System.Guid.TryParseExact(value, "D") with
        | true, id -> DatabaseSession.admitted ActorId.create id
        | _ -> invalid "Invalid reporting account."

    let private actorNames connection groupBy keys token =
        task {
            if groupBy <> Some CaseGrouping.Responsible then
                return Map.empty
            else
                let ids =
                    keys |> List.choose (fst >> Option.map actorId >> Option.map ActorId.value)

                if ids.IsEmpty then
                    return Map.empty
                else
                    let! rows =
                        DatabaseSession.rows
                            connection
                            "SELECT actor_id,principal_kind,issuer,subject,enabled,display_name,contact FROM claimcore.actors WHERE actor_id=ANY($1::uuid[])"
                            [| box (List.toArray ids) |]
                            token

                    return
                        rows
                        |> List.map (Projections.actorRow >> Actor.currentReference)
                        |> List.map (fun actor -> actor.ActorId, actor)
                        |> Map.ofList
        }

    let private groupKey grouping accounts raw =
        let month raw =
            CaseMonth.parse raw |> admitted |> CaseGroupKey.Month

        match grouping, raw with
        | (CaseGrouping.RegisteredMonth | CaseGrouping.ReceivedMonth | CaseGrouping.ClosedMonth),
          Some value -> month value
        | CaseGrouping.Outcome, Some value ->
            Vocabulary.tryParse ClosureOutcome.vocabulary value
            |> Option.map CaseGroupKey.Outcome
            |> Option.defaultWith (fun () -> invalid "Invalid reporting outcome.")
        | CaseGrouping.Responsible, Some value ->
            Map.tryFind (actorId value) accounts
            |> Option.map CaseGroupKey.Responsible
            |> Option.defaultWith (fun () -> invalid "Missing reporting account.")
        | CaseGrouping.DaysOpen, Some value ->
            match
                System.Int32.TryParse(
                    value,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture
                )
            with
            | true, index ->
                Vocabulary.all DaysOpenBand.vocabulary
                |> List.tryItem index
                |> Option.map CaseGroupKey.DaysOpen
                |> Option.defaultWith (fun () -> invalid "Invalid number-of-days group.")
            | _ -> invalid "Invalid number-of-days group."
        | _, None ->
            MissingCaseGroup.forGrouping grouping
            |> Option.map CaseGroupKey.Missing
            |> Option.defaultWith (fun () -> invalid "Missing required report grouping key.")

    let read connection selection groupBy token =
        task {
            let expression, groupParameters = grouping groupBy

            let parameters =
                Array.append (CaseSelectionSql.parameters selection) groupParameters

            let predicate = CaseSelectionSql.predicate selection

            let! rows =
                DatabaseSession.rows
                    connection
                    ($"SELECT c.state,c.status,c.progress,count(*),c.claimed_currency,sum(c.claimed_amount)::text,c.payable_currency,sum(c.payable_amount)::text,({CaseSelectionSql.responsibility}),({CaseSelectionSql.due}),({expression}) FROM claimcore.cases c WHERE {predicate} GROUP BY c.state,c.status,c.progress,c.claimed_currency,c.payable_currency,9,10,11")
                    parameters
                    token

            let! netRows =
                DatabaseSession.rows
                    connection
                    ($"SELECT p.currency,sum(p.paid-p.returned)::text,count(*),({expression}) FROM claimcore.cases c JOIN claimcore.case_payment_totals p ON p.case_id=c.case_id WHERE {predicate} GROUP BY p.currency,4")
                    parameters
                    token

            let payments =
                netRows
                |> List.map (fun row ->
                    DatabaseSession.optional<string> 3 row,
                    total
                        true
                        (DatabaseSession.value<int64> 2 row)
                        (Some(DatabaseSession.value<string> 0 row))
                        (Some(DatabaseSession.value<string> 1 row))
                    |> Option.get)

            let byGroup column =
                List.groupBy (DatabaseSession.optional<string> column)

            let counted = byGroup 10 rows

            let netByGroup =
                payments
                |> List.groupBy fst
                |> List.map (fun (key, values) -> key, List.map snd values)
                |> Map.ofList

            let combine payments =
                CaseWorklistSummary.combineTotals payments
                |> Result.defaultWith (fun _ -> invalid "Invalid exact currency totals.")

            let summary payments cases =
                cases
                |> List.map group
                |> CaseWorklistSummary.fromGroups selection.DateBasis (combine payments)
                |> Result.defaultWith (fun _ -> invalid "Invalid case totals or counts.")

            let overall = summary (payments |> List.map snd) rows

            match groupBy with
            | None -> return overall
            | Some grouping ->
                let! names = actorNames connection groupBy counted token

                let groups =
                    counted
                    |> List.map (fun (key, cases) ->
                        let paid = Map.tryFind key netByGroup |> Option.defaultValue []
                        let calculated = summary paid cases

                        {
                            CaseMoneyGroup.Group = groupKey grouping names key
                            Count = calculated.Counts.Total
                            Claimed = calculated.Claimed
                            Payable = calculated.Payable
                            NetPaid = calculated.NetPaid
                        })
                    |> List.sortBy _.Group

                return
                    { overall with
                        GroupBy = groupBy
                        Groups = groups
                    }
        }
