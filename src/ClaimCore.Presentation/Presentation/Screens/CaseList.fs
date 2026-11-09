namespace ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ScreenEntries

module CaseListScreen =
    let caseSummaryColumns =
        [
            {
                Key = "caseReference"
                LabelId = FieldPresentation.labelId BusinessField.CaseReference
                Prominence = Prominence.Primary
                IsMoney = false
                Read = fun (row: CaseSummary) -> PresentedValue.Reference row.Reference
            }
            {
                Key = "headline"
                LabelId = FieldPresentation.labelId BusinessField.Status
                Prominence = Prominence.Primary
                IsMoney = false
                Read = fun row -> PresentedValue.State row.Headline
            }
            yield!
                caseFields FieldPresentation.declarations (fun (row: CaseSummary) ->
                    CaseState.fields row.State
                    |> Option.map (fun (facts: CaseSummaryFacts) -> facts.Fields))
                |> List.map (fun definition ->
                    { definition with
                        Read =
                            fun row ->
                                if CaseState.lifecycle row.State = Lifecycle.Erased then
                                    PresentedValue.Absent "lifecycle.ERASED.label"
                                else
                                    definition.Read row
                    })
            {
                Key = "responsible"
                LabelId = FieldPresentation.labelId BusinessField.ResponsibleActor
                Prominence = Prominence.Secondary
                IsMoney = false
                Read =
                    fun (row: CaseSummary) ->
                        row.Responsible
                        |> Option.map PresentedValue.Actor
                        |> Option.defaultValue (PresentedValue.Absent "view.UNASSIGNED.label")
            }
            {
                Key = "nextAction"
                LabelId = "field.reminderAction.label"
                Prominence = Prominence.Secondary
                IsMoney = false
                Read =
                    fun row ->
                        row.NextAction
                        |> Option.map (fun next ->
                            PresentedValue.Text(ReminderAction.format next.Action))
                        |> Option.defaultValue (PresentedValue.Absent "view.NO_NEXT_ACTION.label")
            }
            {
                Key = "changedAt"
                LabelId = "label.changedAt"
                Prominence = Prominence.Detail
                IsMoney = false
                Read = fun row -> PresentedValue.Instant row.ChangedAt
            }
        ]

    let private workRevision =
        column
            "workRevision"
            "label.workRevision"
            Prominence.Detail
            false
            (fun (row: CaseSummary) -> PresentedValue.Identifier(Revision.format row.WorkRevision))

    let caseSummary row =
        [
            {
                TitleId = "subject.CASE.label"
                Entries = entries caseSummaryColumns row
            }
        ]

    let rows rows =
        let attention =
            column
                "attention"
                "label.attention"
                Prominence.Secondary
                false
                (fun (row: CaseSummary) -> codes CaseAttention.vocabulary row.Attention)

        let data =
            table
                (fun (row: CaseSummary) -> CaseId.format row.CaseId)
                (fun row ->
                    Some
                        {
                            Summary = None
                            Highlights = []
                            Sections =
                                [ section "label.recordDetails" (entries [ workRevision ] row) ]
                        })
                (caseSummaryColumns @ [ attention ])
                rows

        Screen.create [ region RegionRole.Results None (RegionContent.Table data) ]

    let view (reply: CaseSummaryPage) =
        let screen = rows reply.Items

        let screen =
            match reply.ExactReference with
            | None -> screen
            | Some exact ->
                let includeExact region =
                    match region.Content with
                    | RegionContent.Table data ->
                        let exactSection =
                            section
                                "label.exactReference"
                                [
                                    value
                                        "exactReference"
                                        "field.caseReference.label"
                                        Prominence.Primary
                                        (PresentedValue.Reference exact.CaseReference)
                                ]

                        { region with
                            Content =
                                RegionContent.Table
                                    { data with
                                        Sections = [ exactSection ]
                                    }
                        }
                    | _ -> region

                { screen with
                    Regions = screen.Regions |> List.map includeExact
                }

        page reply.Basis reply.Items.Length reply.NextCursor screen
