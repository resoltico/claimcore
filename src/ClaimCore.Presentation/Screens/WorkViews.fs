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


    let view (reply: WorkViews) =
        let columns =
            [
                {
                    Column.Key = "view"
                    LabelId = "label.views"
                    Prominence = Prominence.Primary
                    IsMoney = false
                }
                {
                    Column.Key = "count"
                    LabelId = "label.total"
                    Prominence = Prominence.Secondary
                    IsMoney = false
                }
            ]

        let caseRows =
            reply.CaseViews
            |> List.map (fun item ->
                {
                    RegionRow.Key = caseKey item.View
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
                    RegionRow.Key = reminderKey item.View
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
            ]
