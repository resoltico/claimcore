namespace ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ScreenEntries

module WorkViewsScreen =
    let workViews (reply: WorkViews) =
        let cases =
            reply.CaseViews
            |> List.map (fun item ->
                let token = CaseView.token item.View
                count token ("view." + token + ".label") item.Count)

        let reminders =
            reply.ReminderViews
            |> List.map (fun item ->
                let token = ReminderView.token item.View
                count token ("view." + token + ".label") item.Count)

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
                    RegionRow.Key = "CASE:" + CaseView.token item.View
                    Values =
                        [
                            PresentedValue.Message(
                                Vocabulary.messageKey CaseView.vocabulary item.View "label"
                            )
                            PresentedValue.Count item.Count
                        ]
                    Detail = None
                })

        let reminderRows =
            reply.ReminderViews
            |> List.map (fun item ->
                {
                    RegionRow.Key = "REMINDER:" + ReminderView.token item.View
                    Values =
                        [
                            PresentedValue.Message(
                                Vocabulary.messageKey ReminderView.vocabulary item.View "label"
                            )
                            PresentedValue.Count item.Count
                        ]
                    Detail = None
                })

        Screen.create
            [
                region
                    RegionRole.Results
                    None
                    (RegionContent.Table
                        {
                            Columns = columns
                            Rows = caseRows @ reminderRows
                            Groups = []
                            Sections = []
                        })
            ]
