namespace ClaimCore.Application.Reads

open ClaimCore.Application.Storage

open ClaimCore.Application.Reads

open ClaimCore.Application.Execution

open ClaimCore.RecordFormat
open System
open System.Threading
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open Npgsql

module internal TransactionPages =
    type PeopleHistoryQuery =
        {
            TargetActorId: ActorId option
            BeforePosition: EffectPosition option
            Limit: int
        }

    type CaseQuery =
        {
            Selection: CaseSelection
            AfterPosition: CaseListPosition option
            Limit: int
        }

    let grantPageSql =
        let grantedAt = DatabaseSession.instantExpression "p.recorded_at"
        ($"SELECT g.role,c.case_reference,g.case_id,l.natural_key,{grantedAt},p.actor_id FROM claimcore.grants g JOIN claimcore.ledger p ON p.sequence=g.granted_sequence LEFT JOIN claimcore.cases c USING(case_id) LEFT JOIN claimcore.ledger_effects l ON l.part_kind='CASE' AND l.part_id=g.case_id AND l.revision=1 WHERE g.actor_id=$1 AND ($2::text IS NULL OR g.role>$2 OR (g.role=$2 AND $3::uuid IS NOT NULL AND (g.case_id>$3 OR g.case_id IS NULL))) ORDER BY g.role ASC,g.case_id ASC NULLS LAST LIMIT $4")

    let page limit position rows =
        let visible = List.truncate limit rows

        {
            Items = visible
            Next =
                if List.length rows > limit then
                    List.tryLast visible |> Option.map position
                else
                    None
        }

    let private caseQuery (query: CaseQuery) =
        let key = "upper(c.case_reference COLLATE \"C\")"

        let after, sorting, values, limit =
            match query.Selection.Filter.Order, query.AfterPosition with
            | CaseListOrder.Reference, position ->
                let reference =
                    match position with
                    | None -> None
                    | Some(CaseListPosition.Reference value) -> Some value
                    | _ -> invalidArg (nameof query) "Expected a reference position."

                key + " > $17 COLLATE \"C\"",
                key + " ASC",
                [| DatabaseSession.parameter reference |],
                "$18"
            | CaseListOrder.Recent, position ->
                let reference, changedAt =
                    match position with
                    | None -> None, None
                    | Some(CaseListPosition.Recent(time, reference)) -> Some reference, Some time
                    | _ -> invalidArg (nameof query) "Expected a recent position."

                "(c.changed_at < $18::timestamptz OR (c.changed_at = $18 AND "
                + key
                + " > $17 COLLATE \"C\"))",
                "c.changed_at DESC," + key + " ASC",
                [| DatabaseSession.parameter reference; DatabaseSession.parameter changedAt |],
                "$19"
            | CaseListOrder.Notified, position ->
                let reference, date =
                    match position with
                    | None -> None, None
                    | Some(CaseListPosition.Notified(date, reference)) -> Some reference, date
                    | _ -> invalidArg (nameof query) "Expected a notification position."

                "(($18::date IS NULL AND c.notified_on IS NULL AND "
                + key
                + " > $17 COLLATE \"C\") OR ($18::date IS NOT NULL AND (c.notified_on > $18::date OR c.notified_on IS NULL OR (c.notified_on=$18::date AND "
                + key
                + " > $17 COLLATE \"C\"))))",
                "c.notified_on ASC NULLS LAST," + key + " ASC",
                [|
                    DatabaseSession.parameter reference
                    DatabaseSession.parameter (date |> Option.map CalendarDate.format)
                |],
                "$19"

            | CaseListOrder.FollowUp, position ->
                let reference, date =
                    match position with
                    | None -> None, None
                    | Some(CaseListPosition.FollowUp(date, reference)) -> Some reference, date
                    | _ -> invalidArg (nameof query) "Expected a follow-up position."

                "(($18::date IS NULL AND c.follow_up IS NULL AND "
                + key
                + " > $17 COLLATE \"C\") OR ($18::date IS NOT NULL AND (c.follow_up > $18::date OR c.follow_up IS NULL OR (c.follow_up=$18::date AND "
                + key
                + " > $17 COLLATE \"C\"))))",
                "c.follow_up ASC NULLS LAST," + key + " ASC",
                [|
                    DatabaseSession.parameter reference
                    DatabaseSession.parameter (date |> Option.map CalendarDate.format)
                |],
                "$19"

        let sql =
            $"SELECT {Projections.caseColumns} FROM claimcore.cases c WHERE {CaseSelectionSql.predicate query.Selection} AND ($17::text IS NULL OR {after}) ORDER BY {sorting} LIMIT {limit}"

        let parameters =
            Array.concat
                [
                    CaseSelectionSql.parameters query.Selection
                    values
                    [| box (query.Limit + 1) |]
                ]

        sql, parameters

    let cases connection installation (query: CaseQuery) token =
        task {
            let sql, parameters = caseQuery query
            let! rows = DatabaseSession.namedRows connection sql parameters token

            let selected =
                rows
                |> List.map (Projections.caseRow installation)
                |> page query.Limit (fun record ->
                    CaseListPosition.create
                        query.Selection.Filter.Order
                        record.Index.ChangedAt
                        record.Index.NotifiedOn
                        record.Index.FollowUp
                        (CaseReference.format record.Snapshot.Reference))
            // Load only visible parents' independently owned work; the lookahead remains financial-only.
            let! work =
                WorkStorage.summaries
                    connection
                    (selected.Items |> List.map _.CaseId)
                    query.Selection.DateBasis.BusinessDate
                    token

            return
                {
                    Items =
                        selected.Items
                        |> List.map (fun record ->
                            {
                                CasePageItem.Case = record
                                WorkSummary = Map.tryFind record.CaseId work
                            })
                    Next = selected.Next
                }
        }

    let peopleHistory connection installation (query: PeopleHistoryQuery) token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT l.operation_id,e.effect_index FROM claimcore.ledger_effects e JOIN claimcore.ledger l ON l.sequence=e.operation_sequence WHERE e.aggregate_kind='PERSON' AND ($1::uuid IS NULL OR e.aggregate_id=$1) AND ($2::bigint IS NULL OR (e.operation_sequence,e.effect_index)<($2::bigint,$3::integer)) ORDER BY e.operation_sequence DESC,e.effect_index DESC LIMIT $4"
                    [|
                        DatabaseSession.parameter (query.TargetActorId |> Option.map ActorId.value)
                        DatabaseSession.parameter (
                            query.BeforePosition
                            |> Option.map (EffectPosition.sequence >> Sequence.value)
                        )
                        DatabaseSession.parameter (
                            query.BeforePosition |> Option.map EffectPosition.index
                        )
                        box (query.Limit + 1)
                    |]
                    token

            let! effects = StoredEntries.bind connection installation rows token
            return page query.Limit (fun (entry: StoredEffect) -> entry.Position) effects
        }

    let actors connection text role after limit token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT actor_id,principal_kind,issuer,subject,enabled,display_name,contact FROM claimcore.actors WHERE ($1::uuid IS NULL OR actor_id>$1) AND ($3::text IS NULL OR (display_name_search LIKE '%'||$3||'%' ESCAPE chr(92) OR contact_search LIKE '%'||$3||'%' ESCAPE chr(92))) AND ($4::text IS NULL OR EXISTS(SELECT 1 FROM claimcore.grants assigned WHERE assigned.actor_id=actors.actor_id AND assigned.role=$4)) ORDER BY actor_id LIMIT $2"
                    [|
                        DatabaseSession.parameter (after |> Option.map ActorId.value)
                        box (limit + 1)
                        DatabaseSession.parameter (
                            text
                            |> Option.map (fun query ->
                                DirectorySearchText.value query
                                |> fun value ->
                                    value
                                        .Replace("\\", "\\\\")
                                        .Replace("%", "\\%")
                                        .Replace("_", "\\_"))
                        )
                        DatabaseSession.parameter (role |> Option.map Role.token)
                    |]
                    token

            return rows |> List.map Projections.actorRow |> page limit (fun actor -> actor.ActorId)
        }

    let grants connection target (after: GrantPosition option) limit token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    grantPageSql
                    [|
                        box (ActorId.value target)
                        DatabaseSession.parameter (
                            after |> Option.map (fun value -> Role.token value.Role)
                        )
                        DatabaseSession.parameter (
                            after |> Option.bind _.CaseId |> Option.map CaseId.value
                        )
                        box (limit + 1)
                    |]
                    token

            let visible = rows |> List.map Projections.grantRow |> page limit snd

            return
                {
                    Items = visible.Items |> List.map fst
                    Next = visible.Next
                }
        }
