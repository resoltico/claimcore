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

    let private basePredicate =
        $"($1 OR {scopedRead}) AND NOT EXISTS(SELECT 1 FROM claimcore.grants bad WHERE bad.actor_id=$2 AND bad.case_id=c.case_id AND bad.role=ANY($4::text[])) AND ($5::text IS NULL OR (c.search_text LIKE '%%' || $5 || '%%' ESCAPE chr(92) OR c.work_search_text LIKE '%%' || $5 || '%%' ESCAPE chr(92)))"

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

        @ (Vocabulary.all CaseAssignment.vocabulary
           |> List.map (fun value ->
               "assignment:" + CaseAssignment.token value, CaseAssignment.token value))
        @ (Vocabulary.all CaseDue.vocabulary
           |> List.map (fun value -> "due:" + CaseDue.token value, CaseDue.token value))

    let private token key =
        let index =
            selectionTokens |> List.findIndex (fun (candidate, _) -> candidate = key)

        $"($8::text[])[{index + 1}]"

    let private available =
        let eligibility =
            AssigneeSelectionSql.predicate
                Access.assigneeSelection
                "$9"
                AssigneeSelectionSql.Source.ResponsibleCase

        $"EXISTS(SELECT 1 FROM claimcore.actors a WHERE a.actor_id=c.responsible_actor AND {eligibility})"

    let datePredicateAt column dateParameter value =
        let comparison =
            match ReminderDue.relation value with
            | DateRelation.Before -> "<"
            | DateRelation.Same -> "="
            | DateRelation.After -> ">"

        $"COALESCE({column}{comparison}{dateParameter}::date,FALSE)"

    let datePredicate column value = datePredicateAt column "$6" value

    /// The adapter interprets finite facts and boolean structure, never view identities.
    let rec private interpret responsible =
        function
        | WorkSelection.All values ->
            if values.IsEmpty then
                "TRUE"
            else
                values
                |> List.map (interpret responsible >> fun value -> "(" + value + ")")
                |> String.concat " AND "
        | WorkSelection.Any values ->
            if values.IsEmpty then
                "FALSE"
            else
                values
                |> List.map (interpret responsible >> fun value -> "(" + value + ")")
                |> String.concat " OR "
        | WorkSelection.Not value -> "NOT (" + interpret responsible value + ")"
        | WorkSelection.Fact predicate ->
            let equal column key = $"COALESCE({column}={token key},FALSE)"

            match predicate with
            | WorkPredicate.Lifecycle value ->
                equal "c.state" ("lifecycle:" + Lifecycle.token value)
            | WorkPredicate.Status value -> equal "c.status" ("status:" + CaseStatus.token value)
            | WorkPredicate.Progress value ->
                equal "c.progress" ("progress:" + CaseProgress.token value)
            | WorkPredicate.CaseDate(filter, _) ->
                let date, bound = CaseDateFilter.bound filter

                let column =
                    match date with
                    | CaseReportDate.Registered -> "c.registered_on"
                    | CaseReportDate.Received -> "c.received_on"
                    | CaseReportDate.Closed -> "c.closed_on"
                    | CaseReportDate.LastActivity -> "c.last_activity_on"

                let operator =
                    match bound with
                    | CaseDateBound.From -> ">="
                    | CaseDateBound.Through -> "<="

                let index = 1 + List.findIndex ((=) filter) CaseDateFilter.all
                let parameter = $"($10::text[])[{index}]::date"
                $"({parameter} IS NULL OR {column}{operator}{parameter})"
            | WorkPredicate.Mine -> "COALESCE(c.responsible_actor=$2,FALSE)"
            | WorkPredicate.Responsible value -> responsible value
            | WorkPredicate.Unassigned -> "c.responsible_actor IS NULL"
            | WorkPredicate.AssigneeUnavailable ->
                $"c.responsible_actor IS NOT NULL AND NOT ({available})"
            | WorkPredicate.PaymentsNeedReview -> "COALESCE(c.payment_review,FALSE)"
            | WorkPredicate.NoNextDue -> "c.follow_up IS NULL"
            | WorkPredicate.NextDue value -> datePredicate "c.follow_up" value
            | WorkPredicate.NextDeadlineDue value -> datePredicate "c.deadline_due" value
            | WorkPredicate.ReminderState value ->
                equal "r.state" ("reminder:" + ReminderState.token value)
            | WorkPredicate.ReminderDue value -> datePredicate "r.due" value

    let selectionPredicate selection =
        interpret
            (fun _ ->
                invalidArg
                    (nameof selection)
                    "A supplied actor filter needs its parameter binding.")
            selection

    let viewPredicate view =
        CaseView.plan view |> selectionPredicate

    let private classification vocabulary tokenKey plan =
        let cases =
            Vocabulary.all vocabulary
            |> List.map (fun value ->
                let condition = selectionPredicate (plan value)
                let result = token (tokenKey value)
                $"WHEN {condition} THEN {result}")
            |> String.concat " "

        $"CASE {cases} ELSE NULL END"

    let responsibility =
        classification
            CaseAssignment.vocabulary
            (fun value -> "assignment:" + CaseAssignment.token value)
            CaseAssignment.plan

    let due =
        classification CaseDue.vocabulary (fun value -> "due:" + CaseDue.token value) CaseDue.plan

    let predicate (selection: CaseSelection) =
        let filters =
            CaseListFilter.selection selection.Filter
            |> interpret (fun actor ->
                if actor <> selection.Filter.ResponsibleActor then
                    invalidArg (nameof selection) "Actor binding differs from its filter."

                "($7::uuid IS NULL OR c.responsible_actor=$7)")

        $"{basePredicate} AND ({filters})"

    /// Suffix parameters follow the complete shared selection prefix.
    let suffix index = "$" + string (10 + index)

    let parameters (selection: CaseSelection) =
        let filter = selection.Filter

        let text =
            filter.Text |> Option.map (CaseSearchText.value >> LikePattern.escapeLiteral)

        [|
            box (selection.Visibility = CaseVisibility.Every)
            box (ActorId.value selection.ActorId)
            box (selection.ReadRoles |> List.map Role.token |> List.toArray)
            box (selection.ForbiddenRoles |> List.map Role.token |> List.toArray)
            DatabaseSession.parameter text
            box selection.DateBasis.BusinessDate
            DatabaseSession.parameter (filter.ResponsibleActor |> Option.map ActorId.value)
            box (selectionTokens |> List.map snd |> List.toArray)
            AssigneeSelectionSql.parameters Access.assigneeSelection
            box (
                CaseDateFilter.all
                |> List.map (fun date ->
                    filter.Dates
                    |> Map.tryFind date
                    |> Option.map CalendarDate.format
                    |> Option.toObj)
                |> List.toArray
            )
        |]
