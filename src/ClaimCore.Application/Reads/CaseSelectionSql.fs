namespace ClaimCore.Application.Reads

open ClaimCore.Application.Storage

open ClaimCore.Application.Execution

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

    let private basePredicate =
        $"($1 OR {scopedRead}) AND NOT EXISTS(SELECT 1 FROM claimcore.grants bad WHERE bad.actor_id=$2 AND bad.case_id=c.case_id AND bad.role=ANY($4::text[])) AND (cardinality($5::text[])=0 OR c.state=ANY($5::text[])) AND (cardinality($6::text[])=0 OR c.status=ANY($6::text[])) AND (cardinality($7::text[])=0 OR c.progress=ANY($7::text[])) AND ($8::text IS NULL OR (c.search_text LIKE '%%' || $8 || '%%' ESCAPE chr(92) OR c.work_search_text LIKE '%%' || $8 || '%%' ESCAPE chr(92))) AND ($9::text IS NULL OR ({responsibility})=$9) AND ($10::text IS NULL OR ({due})=$10)"

    let private selectionTokens =
        [
            yield!
                Vocabulary.all Lifecycle.vocabulary
                |> List.map (fun value ->
                    "lifecycle:" + Lifecycle.token value, Lifecycle.token value)
            yield!
                Vocabulary.all CaseStatus.vocabulary
                |> List.map (fun value ->
                    "status:" + CaseStatus.token value, CaseStatus.token value)
            yield!
                CaseProgress.vocabulary
                |> Vocabulary.all
                |> List.map (fun value ->
                    "progress:" + CaseProgress.token value, CaseProgress.token value)
            yield!
                Vocabulary.all ReminderState.vocabulary
                |> List.map (fun value ->
                    "reminder:" + ReminderState.token value, ReminderState.token value)
        ]

    let private token key =
        let index =
            selectionTokens |> List.findIndex (fun (candidate, _) -> candidate = key)

        $"($13::text[])[{index + 1}]"

    let private available =
        "EXISTS(SELECT 1 FROM claimcore.actors a WHERE a.actor_id=c.responsible_actor AND a.enabled AND a.principal_kind=$16::text AND EXISTS(SELECT 1 FROM claimcore.grants g WHERE g.actor_id=a.actor_id AND (g.case_id IS NULL OR g.case_id=c.case_id) AND g.role=ANY($14::text[])) AND EXISTS(SELECT 1 FROM claimcore.grants g WHERE g.actor_id=a.actor_id AND (g.case_id IS NULL OR g.case_id=c.case_id) AND g.role=ANY($15::text[])))"

    /// The adapter interprets finite facts and boolean structure, never view identities.
    let rec selectionPredicate =
        function
        | WorkSelection.All values ->
            if values.IsEmpty then
                "TRUE"
            else
                values
                |> List.map (selectionPredicate >> fun value -> "(" + value + ")")
                |> String.concat " AND "
        | WorkSelection.Any values ->
            if values.IsEmpty then
                "FALSE"
            else
                values
                |> List.map (selectionPredicate >> fun value -> "(" + value + ")")
                |> String.concat " OR "
        | WorkSelection.Not value -> "NOT (" + selectionPredicate value + ")"
        | WorkSelection.Fact predicate ->
            let equal column key = $"COALESCE({column}={token key},FALSE)"

            let due column =
                function
                | ReminderDue.Overdue -> $"COALESCE({column}<$11::date,FALSE)"
                | ReminderDue.Today -> $"COALESCE({column}=$11::date,FALSE)"
                | ReminderDue.Upcoming -> $"COALESCE({column}>$11::date,FALSE)"

            match predicate with
            | WorkPredicate.Lifecycle value ->
                equal "c.state" ("lifecycle:" + Lifecycle.token value)
            | WorkPredicate.Status value -> equal "c.status" ("status:" + CaseStatus.token value)
            | WorkPredicate.Progress value ->
                equal "c.progress" ("progress:" + CaseProgress.token value)
            | WorkPredicate.Mine -> "COALESCE(c.responsible_actor=$2,FALSE)"
            | WorkPredicate.Unassigned -> "c.responsible_actor IS NULL"
            | WorkPredicate.AssigneeUnavailable ->
                $"c.responsible_actor IS NOT NULL AND NOT ({available})"
            | WorkPredicate.NoNextDue -> "c.follow_up IS NULL"
            | WorkPredicate.NextDue value -> due "c.follow_up" value
            | WorkPredicate.ReminderState value ->
                equal "r.state" ("reminder:" + ReminderState.token value)
            | WorkPredicate.ReminderDue value -> due "r.due" value

    let viewPredicate view =
        CaseView.plan view |> selectionPredicate

    let predicate (selection: CaseSelection) =
        let view =
            selection.Filter.View |> Option.map viewPredicate |> Option.defaultValue "TRUE"

        $"{basePredicate} AND ({view}) AND ($12::uuid IS NULL OR c.responsible_actor=$12)"

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
            box (selection.ReadRoles |> List.map Role.token |> List.toArray)
            box (selection.ForbiddenRoles |> List.map Role.token |> List.toArray)
            box (filter.Lifecycles |> List.map Lifecycle.token |> List.toArray)
            box (filter.Statuses |> List.map CaseStatus.token |> List.toArray)
            box (filter.Progress |> List.map CaseProgress.token |> List.toArray)
            DatabaseSession.parameter text
            DatabaseSession.parameter (filter.Assignment |> Option.map CaseAssignment.token)
            DatabaseSession.parameter (filter.Due |> Option.map CaseDue.token)
            box selection.DateBasis.BusinessDate
            DatabaseSession.parameter (filter.ResponsibleActor |> Option.map ActorId.value)
            box (selectionTokens |> List.map snd |> List.toArray)
            box (Access.rolesFor Capability.ReadCase |> List.map Role.token |> List.toArray)
            box (Access.rolesFor Capability.ManageCaseWork |> List.map Role.token |> List.toArray)
            box (Vocabulary.token PrincipalKind.vocabulary PrincipalKind.Human)
        |]
