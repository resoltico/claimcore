namespace ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ScreenEntries

module WorkViewsScreen =
    let caseKey view = "CASE:" + CaseView.token view
    let reminderKey view = "REMINDER:" + ReminderView.token view

    let labelId token = "view." + token + ".label"

    let workViews (reply: WorkViews) =
        let cases =
            reply.CaseViews
            |> List.map (fun item ->
                let token = CaseView.token item.View
                count token (labelId token) item.Count)

        let reminders =
            reply.ReminderViews
            |> List.map (fun item ->
                let token = ReminderView.token item.View
                count token (labelId token) item.Count)

        [ section "label.cases" cases; section "label.reminders" reminders ]


    let caseProminence =
        function
        | CaseView.NeedsAttention
        | CaseView.Mine
        | CaseView.Overdue
        | CaseView.DueToday
        | CaseView.All -> Prominence.Primary
        | _ -> Prominence.Secondary

    let reminderProminence =
        function
        | ReminderView.Pending
        | ReminderView.Mine
        | ReminderView.Overdue
        | ReminderView.Today
        | ReminderView.Upcoming -> Prominence.Primary
        | ReminderView.Completed
        | ReminderView.Cancelled -> Prominence.Secondary

    let view (reply: WorkViews) =
        let columns =
            [
                {
                    Column.Key = "view"
                    Recorded = false
                    Joins = None
                    LabelId = "label.views"
                    Prominence = Prominence.Primary
                    IsMoney = false
                }
                {
                    Column.Key = "count"
                    Recorded = false
                    Joins = None
                    LabelId = "label.total"
                    Prominence = Prominence.Secondary
                    IsMoney = false
                }
            ]

        let caseRows =
            reply.CaseViews
            |> List.map (fun item ->
                {
                    Choice = None
                    RegionRow.Key = caseKey item.View
                    Transfers = []
                    Recommended = None
                    Prominence = Some(caseProminence item.View)
                    Link =
                        Some(
                            Link.CaseList
                                {
                                    Page =
                                        {
                                            AfterCursor = None
                                            Limit = Paging.DefaultPageSize
                                        }
                                    Filter =
                                        { CaseListFilter.empty<string> with
                                            View = Some item.View
                                        }
                                }
                        )
                    Actions = []
                    Values =
                        [
                            PresentedValue.Message(labelId (CaseView.token item.View))
                            PresentedValue.Count item.Count
                        ]
                    Detail = None
                })

        let reminderRows =
            reply.ReminderViews
            |> List.map (fun item ->
                {
                    Choice = None
                    RegionRow.Key = reminderKey item.View
                    Transfers = []
                    Recommended = None
                    Prominence = Some(reminderProminence item.View)
                    Link =
                        Some(
                            Link.ReminderList
                                {
                                    View = Some item.View
                                    Target = None
                                    State = None
                                    Kind = None
                                    DeadlineOutcome = None
                                    Due = None
                                    Assignment = None
                                    Text = None
                                    AfterCursor = None
                                    Limit = Paging.DefaultPageSize
                                    IncludeInactive = None
                                }
                        )
                    Actions = []
                    Values =
                        [
                            PresentedValue.Message(labelId (ReminderView.token item.View))
                            PresentedValue.Count item.Count
                        ]
                    Detail = None
                })

        let navigation =
            [
                match WorkViewCounts.casesNeedingAttention reply with
                | Some value -> yield count "cases" "label.cases" value
                | None -> ()
                match WorkViewCounts.remindersNeedingAttention reply with
                | Some value -> yield count "reminders" "label.reminders" value
                | None -> ()
            ]

        Screen.create
            [
                if not navigation.IsEmpty then
                    yield entryRegion RegionRole.Totals None navigation
                yield
                    region
                        RegionRole.Results
                        None
                        (RegionContent.Table
                            {
                                Columns = columns
                                Rows = caseRows @ reminderRows
                                Groups = []
                                Sections = []
                                Meter = None
                            })
                    |> withEmpty "empty.workViews"
            ]

    let document (value: WorkViews) =
        let links =
            (value.CaseViews
             |> List.map (fun item ->
                 NamedLink.create
                     (labelId (CaseView.token item.View))
                     LinkRelation.View
                     (Link.caseList (Some item.View))))
            @ (value.ReminderViews
               |> List.map (fun item ->
                   NamedLink.create
                       (labelId (ReminderView.token item.View))
                       LinkRelation.View
                       (Link.reminderList item.View)))

        HumanDocument.snapshot
            value.Basis
            (view value)
            DocumentSubject.List
            []
            links
            None
            HumanDisclosure.Current
            None
