namespace ClaimCore.Postgres

open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat

/// Listing and aggregation use the same scoped row selection before paging or grouping.
module internal CaseSelectionSql =
    let private scopedRead =
        "EXISTS(SELECT 1 FROM claimcore.grants g WHERE g.actor_id=$2 AND g.case_id=c.case_id AND g.role=ANY($3::text[]))"

    let responsibility =
        "CASE WHEN c.responsible_actor=$2 THEN 'MINE' WHEN c.responsible_actor IS NULL AND c.state<>'ERASED' THEN 'UNASSIGNED' ELSE NULL END"

    let due =
        "CASE WHEN c.state<>'ACTIVE' OR c.status IS DISTINCT FROM 'OPENED' THEN 'INACTIVE' WHEN c.follow_up IS NULL THEN 'UNSCHEDULED' WHEN c.follow_up<$11::date THEN 'OVERDUE' WHEN c.follow_up=$11::date THEN 'TODAY' ELSE 'UPCOMING' END"

    let predicate =
        $"($1 OR {scopedRead}) AND NOT EXISTS(SELECT 1 FROM claimcore.grants bad WHERE bad.actor_id=$2 AND bad.case_id=c.case_id AND bad.role=ANY($4::text[])) AND (cardinality($5::text[])=0 OR c.state=ANY($5::text[])) AND (cardinality($6::text[])=0 OR c.status=ANY($6::text[])) AND (cardinality($7::text[])=0 OR c.progress=ANY($7::text[])) AND ($8::text IS NULL OR c.search_text LIKE '%%' || $8 || '%%' ESCAPE chr(92)) AND ($9::text IS NULL OR ({responsibility})=$9) AND ($10::text IS NULL OR ({due})=$10)"

    let parameters (selection: CaseSelection) =
        let filter = selection.Filter

        let text =
            filter.Text
            |> Option.map (fun value ->
                (CaseSearchText.value value)
                    .Replace("\\", "\\\\")
                    .Replace("%", "\\%")
                    .Replace("_", "\\_"))

        [|
            box (selection.Visibility = CaseVisibility.Every)
            box (ActorId.value selection.ActorId)
            box (selection.ReadRoles |> List.map DurableNames.role |> List.toArray)
            box (selection.ForbiddenRoles |> List.map DurableNames.role |> List.toArray)
            box (filter.Lifecycles |> List.map DurableNames.lifecycle |> List.toArray)
            box (filter.Statuses |> List.map DurableNames.status |> List.toArray)
            box (filter.Progress |> List.map DurableNames.progress |> List.toArray)
            DatabaseSession.parameter text
            DatabaseSession.parameter (filter.Assignment |> Option.map CaseAssignment.token)
            DatabaseSession.parameter (filter.Due |> Option.map CaseDue.token)
            box selection.DateBasis.BusinessDate
        |]
