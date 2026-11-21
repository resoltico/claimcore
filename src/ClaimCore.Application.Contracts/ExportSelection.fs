namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type ExportSelection =
    | Cases of CaseListFilter<string>
    | CaseSummary of CaseSummarySelection
    | Timeline of CaseTimelineRequest
    | Reminders of ReminderListRequest
    | PeopleHistory of PeopleHistoryRequest

module ExportSelection =
    let query =
        function
        | ExportSelection.Cases _ -> QueryTaskId.CaseList
        | ExportSelection.CaseSummary _ -> QueryTaskId.CaseSummary
        | ExportSelection.Timeline _ -> QueryTaskId.CaseTimeline
        | ExportSelection.Reminders _ -> QueryTaskId.ReminderList
        | ExportSelection.PeopleHistory _ -> QueryTaskId.PeopleHistory

    let private optional key render =
        Option.toList >> List.map (fun value -> key, render value)

    let private choices key token values =
        match values |> List.map token |> Set.ofList |> Set.toList with
        | [] -> []
        | values -> [ key, String.concat "," values ]

    let private flag value = if value then "true" else "false"

    let private identifier (value: System.Guid) = value.ToString("D")

    let private textFilter = optional "textFilter" (fun (_: string) -> flag true)

    let private target =
        function
        | CaseSelector.Id id -> [ "caseId", CaseId.format id ]
        | CaseSelector.Reference _ -> [ "caseReferenceFilter", flag true ]

    let private caseFilter (filter: CaseListFilter<string>) =
        [
            yield! choices "lifecycles" Lifecycle.token filter.Lifecycles
            yield! choices "statuses" CaseStatus.token filter.Statuses
            yield! choices "progress" CaseProgress.token filter.Progress
            yield! textFilter filter.Text
            "order", CaseListOrder.token filter.Order
            yield! optional "view" CaseView.token filter.View
            yield! optional "responsibleActorId" ActorId.format filter.ResponsibleActor
            yield! optional "assignment" CaseAssignment.token filter.Assignment
            yield! optional "due" CaseDue.token filter.Due
            for KeyValue(field, date) in filter.Dates do
                CaseDateFilter.token field, CalendarDate.format date
        ]

    /// Audit metadata admits typed selectors only. Authored text and references are represented by presence flags.
    let disclosure rowCount resolvedCase selection =
        let selectionFields =
            match selection with
            | ExportSelection.Cases filter -> caseFilter filter
            | ExportSelection.CaseSummary request ->
                caseFilter request.Filter
                @ optional "groupBy" (Vocabulary.token CaseGrouping.vocabulary) request.GroupBy
            | ExportSelection.Timeline request ->
                target request.Target @ optional "filter" TimelineFilter.token request.Filter
            | ExportSelection.Reminders request ->
                [
                    yield! request.Target |> Option.toList |> List.collect target
                    yield! optional "state" ReminderStateSelection.token request.State
                    yield!
                        optional
                            "kind"
                            (Vocabulary.token ReminderKindSelection.vocabulary)
                            request.Kind
                    yield! optional "deadlineOutcome" DeadlineOutcome.token request.DeadlineOutcome
                    yield! optional "view" ReminderView.token request.View
                    yield! optional "due" ReminderDue.token request.Due
                    yield! optional "assignment" CaseAssignment.token request.Assignment
                    yield! textFilter request.Text
                    yield! optional "includeInactive" flag request.IncludeInactive
                ]
            | ExportSelection.PeopleHistory request ->
                optional "actorId" identifier request.TargetActorId

        let fields =
            selectionFields
            |> Map.ofList
            |> fun fields ->
                match resolvedCase with
                | Some id -> Map.add "caseId" (CaseId.format id) fields
                | None -> fields

        {
            Query = query selection |> QueryTaskId.key
            Selection = fields
            RowCount = rowCount
        }
