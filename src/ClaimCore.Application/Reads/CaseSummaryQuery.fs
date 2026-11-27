namespace ClaimCore.Application.Reads

open ClaimCore.Application.Storage

open ClaimCore.Application.Execution

open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open System

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
                DatabaseSession.optional<string> 4 row
                |> Option.map (fun value ->
                    Vocabulary.tryParse CaseAssignment.vocabulary value
                    |> Option.defaultWith (fun () ->
                        raise (RecordCorruptException "Invalid responsibility aggregate.")))
            Due =
                Vocabulary.tryParse CaseDue.vocabulary (DatabaseSession.value<string> 5 row)
                |> Option.defaultWith (fun () ->
                    raise (RecordCorruptException "Invalid due aggregate."))
            Lifecycle = lifecycle
            Status = status
            Progress = progress
            Count = count

        }

    let private grouping =
        function
        | None -> "''::text", [||]
        | Some CaseGrouping.RegisteredMonth -> "to_char(c.registered_on,'YYYY-MM')", [||]
        | Some CaseGrouping.ReceivedMonth -> "to_char(c.received_on,'YYYY-MM')", [||]
        | Some CaseGrouping.ClosedMonth -> "to_char(c.closed_on,'YYYY-MM')", [||]
        | Some CaseGrouping.Outcome ->
            let parameter = CaseSelectionSql.suffix 1

            $"array_position({parameter}::text[],c.outcome)::text",
            [| box (Vocabulary.tokens ClosureOutcome.vocabulary |> List.toArray) |]
        | Some CaseGrouping.Responsible -> "c.responsible_actor::text", [||]
        | Some CaseGrouping.DaysOpen ->
            let bounds = DaysOpenBand.bounds |> List.map snd |> List.toArray
            let parameter = CaseSelectionSql.suffix 1

            let cases =
                DaysOpenBand.bounds
                |> List.mapi (fun index _ ->
                    $"WHEN (COALESCE(c.closed_on,$6::date)-c.registered_on) <= ({parameter}::integer[])[{index + 1}] THEN '{index + 1}'")
                |> String.concat " "

            $"CASE {cases} ELSE '{bounds.Length + 1}' END", [| box bounds |]

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
                    keys
                    |> List.filter ((<>) CaseReportPosition.missing)
                    |> List.distinct
                    |> List.map (actorId >> ActorId.value)

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
            CaseReportPosition.ranked value (Vocabulary.all ClosureOutcome.vocabulary)
            |> Option.map CaseGroupKey.Outcome
            |> Option.defaultWith (fun () -> invalid "Invalid reporting outcome.")
        | CaseGrouping.Responsible, Some value ->
            Map.tryFind (actorId value) accounts
            |> Option.map CaseGroupKey.Responsible
            |> Option.defaultWith (fun () -> invalid "Missing reporting account.")
        | CaseGrouping.DaysOpen, Some value ->
            CaseReportPosition.ranked value (Vocabulary.all DaysOpenBand.vocabulary)
            |> Option.map CaseGroupKey.DaysOpen
            |> Option.defaultWith (fun () -> invalid "Invalid number-of-days group.")
        | _, None ->
            MissingCaseGroup.forGrouping grouping
            |> Option.map CaseGroupKey.Missing
            |> Option.defaultWith (fun () -> invalid "Missing required report grouping key.")

    // Report pages promise stable aggregate content. Include the contribution of each
    // selected case and any reported responsible label. Scope filtering happens first.
    let witness connection selection groupBy token =
        let expression, parameters = grouping groupBy

        let responsibleLabel =
            if groupBy = Some CaseGrouping.Responsible then
                "(SELECT jsonb_build_array(a.display_name,a.enabled,a.principal_kind) FROM claimcore.actors a WHERE a.actor_id=c.responsible_actor)"
            else
                "NULL::jsonb"

        let sql =
            $"""
SELECT jsonb_build_array(c.case_id,({expression}),({CaseSelectionSql.responsibility}),
       ({CaseSelectionSql.due}),c.state,c.status,c.progress,c.claimed_currency,c.claimed_amount,
       c.payable_currency,c.payable_amount,({responsibleLabel}),
       (SELECT jsonb_agg(jsonb_build_array(p.currency,p.paid-p.returned) ORDER BY p.currency COLLATE "C")
        FROM claimcore.case_payment_totals p WHERE p.case_id=c.case_id))::text
FROM claimcore.cases c WHERE {CaseSelectionSql.predicate selection} ORDER BY c.case_id
"""

        DatabaseSession.witness
            connection
            sql
            (Array.append (CaseSelectionSql.parameters selection) parameters)
            token

    let private readRows
        connection
        selection
        groupBy
        (after: CaseReportPosition option)
        limit
        token
        =
        task {
            let expression, groupParameters = grouping groupBy

            let parameter offset =
                CaseSelectionSql.suffix (groupParameters.Length + offset)

            let afterGroup, afterCurrency, fetch, overall =
                parameter 1, parameter 2, parameter 3, parameter 4

            let missing = parameter 5

            let parameters =
                Array.concat
                    [
                        CaseSelectionSql.parameters selection
                        groupParameters
                        [|
                            DatabaseSession.parameter (after |> Option.map _.GroupOrder)
                            DatabaseSession.parameter (after |> Option.map _.CurrencyOrder)
                            box (limit + 1)
                            box groupBy.IsNone
                            box CaseReportPosition.missing
                        |]
                    ]

            let predicate = CaseSelectionSql.predicate selection
            // Form scope once. Each contribution joins at its own grain before summation.
            let sql =
                $"""
WITH selected AS MATERIALIZED (
    SELECT c.case_id,c.state,c.status,c.progress,c.claimed_currency,c.claimed_amount,
           c.payable_currency,c.payable_amount,
           ({CaseSelectionSql.responsibility}) AS responsibility,({CaseSelectionSql.due}) AS due,
           COALESCE(({expression}),{missing}::text) AS group_key
    FROM claimcore.cases c WHERE {predicate}
), facets AS (
    SELECT state,status,progress,count(*) AS cases,responsibility,due
    FROM selected GROUP BY state,status,progress,responsibility,due
), contributions AS (
    SELECT group_key,claimed_currency AS currency,claimed_amount AS claimed,NULL::numeric AS payable,NULL::numeric AS net
    FROM selected WHERE claimed_currency IS NOT NULL
    UNION ALL
    SELECT group_key,payable_currency,NULL,payable_amount,NULL FROM selected WHERE payable_currency IS NOT NULL
    UNION ALL
    SELECT s.group_key,p.currency,NULL,NULL,p.paid-p.returned
    FROM selected s JOIN claimcore.case_payment_totals p ON p.case_id=s.case_id
), amounts AS (
    SELECT group_key,currency,sum(claimed)::text AS claimed,count(claimed) AS claimed_cases,
           sum(payable)::text AS payable,count(payable) AS payable_cases,
           sum(net)::text AS net,count(net) AS net_cases
    FROM contributions GROUP BY group_key,currency
), group_counts AS (
    SELECT group_key,count(*) AS cases FROM selected GROUP BY group_key
    UNION ALL
    SELECT '',0 WHERE {overall}::boolean AND NOT EXISTS (SELECT 1 FROM selected)
), report_page AS (
    SELECT g.group_key,COALESCE(a.currency,'') AS currency,g.cases,
           a.claimed,a.claimed_cases,a.payable,a.payable_cases,a.net,a.net_cases
    FROM group_counts g LEFT JOIN amounts a USING(group_key)
    WHERE {afterGroup}::text IS NULL OR
        (g.group_key COLLATE "C",COALESCE(a.currency,'') COLLATE "C") >
        ({afterGroup}::text COLLATE "C",{afterCurrency}::text COLLATE "C")
    ORDER BY g.group_key COLLATE "C",COALESCE(a.currency,'') COLLATE "C" LIMIT {fetch}
)
SELECT * FROM (
    SELECT state,status,progress,cases,responsibility,due,
           NULL::text AS group_key,NULL::text AS currency,
           NULL::text AS claimed,NULL::bigint AS claimed_cases,
           NULL::text AS payable,NULL::bigint AS payable_cases,NULL::text AS net,NULL::bigint AS net_cases
    FROM facets
    UNION ALL
    SELECT NULL,NULL,NULL,cases,NULL,NULL,group_key,currency,claimed,claimed_cases,payable,payable_cases,net,net_cases
    FROM report_page
) combined
ORDER BY group_key COLLATE "C" NULLS FIRST,currency COLLATE "C" NULLS FIRST
"""

            let! rows = DatabaseSession.rows connection sql parameters token

            let facets, reports =
                rows |> List.partition (DatabaseSession.optional<string> 6 >> Option.isNone)

            if reports.Length > limit + 1 then
                invalid "Report rows exceeded the PostgreSQL page bound."

            let keys = reports |> List.map (DatabaseSession.value<string> 6)
            let! names = actorNames connection groupBy keys token

            let position row =
                {
                    GroupOrder = DatabaseSession.value<string> 6 row
                    CurrencyOrder = DatabaseSession.value<string> 7 row
                }

            let data row =
                let key = position row

                let currency =
                    if key.CurrencyOrder = "" then
                        None
                    else
                        Some key.CurrencyOrder

                let amount column signed =
                    match DatabaseSession.optional<string> column row with
                    | Some value ->
                        total
                            signed
                            (DatabaseSession.value<int64> (column + 1) row)
                            currency
                            (Some value)
                    | None -> None

                {
                    Group =
                        groupBy
                        |> Option.map (fun grouping ->
                            groupKey
                                grouping
                                names
                                (if key.GroupOrder = CaseReportPosition.missing then
                                     None
                                 else
                                     Some key.GroupOrder))
                    Count = DatabaseSession.value<int64> 3 row
                    Currency = currency |> Option.map (Currency.parse >> admitted)
                    Claimed = amount 8 false
                    Payable = amount 10 false
                    NetPaid = amount 12 true
                }

            let counts = facets |> List.map group |> CaseWorklistCounts.fromGroups |> admitted

            return counts, List.map (fun row -> position row, data row) reports
        }

    let private maximumCachedRows = 128
    let private maximumCachedReports = 16
    let private cacheLifetime = TimeSpan.FromMinutes 1.
    let private cacheLock = obj ()

    let mutable private reports =
        Map.empty<
            string * string,
            DateTimeOffset * (CaseWorklistCounts * (CaseReportPosition * CaseMoneyRow) list)
         >

    let private page after limit (counts, rows) =
        let remaining =
            match after with
            | None -> rows
            | Some position ->
                rows
                |> List.filter (fun ((row: CaseReportPosition), _) ->
                    let group = String.CompareOrdinal(row.GroupOrder, position.GroupOrder)

                    group > 0
                    || (group = 0
                        && String.CompareOrdinal(row.CurrencyOrder, position.CurrencyOrder) > 0))

        let visible = List.truncate limit remaining

        let next =
            if remaining.Length > limit then
                List.tryLast visible |> Option.map fst
            else
                None

        counts, List.map snd visible, next

    let private prune observed =
        reports <-
            reports
            |> Map.filter (fun _ (stored, _) ->
                stored <= observed && observed - stored < cacheLifetime)

    let read connection selection groupBy after limit cacheKey observed token =
        task {
            let cached =
                lock cacheLock (fun () ->
                    prune observed
                    cacheKey |> Option.bind (fun key -> Map.tryFind key reports |> Option.map snd))

            match cached with
            | Some values -> return page after limit values
            | None ->
                let fill = cacheKey.IsSome && after.IsNone
                let fetch = if fill then maximumCachedRows else limit
                let! data = readRows connection selection groupBy after fetch token
                let _, rows = data

                match cacheKey with
                | Some key when fill && rows.Length <= maximumCachedRows ->
                    lock cacheLock (fun () ->
                        prune observed

                        if reports.Count >= maximumCachedReports then
                            let oldest = reports |> Map.toSeq |> Seq.minBy (snd >> fst) |> fst
                            reports <- Map.remove oldest reports

                        reports <- Map.add key (observed, data) reports)
                | _ -> ()

                // On a database miss the provider has already applied the continuation position.
                return page None limit data
        }
