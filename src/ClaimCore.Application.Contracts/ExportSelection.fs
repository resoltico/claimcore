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
    let private exportQuery =
        function
        | ExportSelection.Cases _ -> DisclosureExportQuery.Cases
        | ExportSelection.CaseSummary _ -> DisclosureExportQuery.CaseSummary
        | ExportSelection.Timeline _ -> DisclosureExportQuery.Timeline
        | ExportSelection.Reminders _ -> DisclosureExportQuery.Reminders
        | ExportSelection.PeopleHistory _ -> DisclosureExportQuery.PeopleHistory

    let query = exportQuery >> DisclosureExportQuery.task

    let private caseFiltered (filter: CaseListFilter<string>) =
        not filter.Lifecycles.IsEmpty
        || not filter.Statuses.IsEmpty
        || not filter.Progress.IsEmpty
        || filter.Text.IsSome
        || filter.View.IsSome
        || filter.ResponsibleActor.IsSome
        || filter.Assignment.IsSome
        || filter.Due.IsSome
        || not filter.Dates.IsEmpty

    /// Store admitted selection shape and counts without authored values or selector IDs.
    let disclosure rowCount paged selection =
        let filtered, order, grouping =
            match selection with
            | ExportSelection.Cases filter -> caseFiltered filter, Some filter.Order, None
            | ExportSelection.CaseSummary request ->
                caseFiltered request.Filter, Some request.Filter.Order, request.GroupBy
            | ExportSelection.Timeline request -> request.Filter.IsSome, None, None
            | ExportSelection.Reminders request ->
                (request.Target.IsSome
                 || request.State.IsSome
                 || request.Kind.IsSome
                 || request.DeadlineOutcome.IsSome
                 || request.View.IsSome
                 || request.Due.IsSome
                 || request.Assignment.IsSome
                 || request.Text.IsSome
                 || request.IncludeInactive.IsSome),
                None,
                None
            | ExportSelection.PeopleHistory request -> request.TargetActorId.IsSome, None, None

        {
            Query = exportQuery selection
            Selection =
                {
                    Filtered = filtered
                    Paged = paged
                    Order = order
                    Grouping = grouping
                }
            RowCount = rowCount
        }
