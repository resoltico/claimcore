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
                LabelId = "label.caseState"
                Prominence = Prominence.Primary
                IsMoney = false
                Read =
                    fun row ->
                        let outcome = CaseState.fields row.State |> Option.bind _.ClosureOutcome
                        headline row.Headline outcome
            }
            for role, key, label in
                [
                    PartyRole.Claimant, "claimants", "label.claimants"
                    PartyRole.Insurer, "insurers", "label.insurers"
                ] do
                let prominence =
                    if role = PartyRole.Claimant then
                        Prominence.Primary
                    else
                        Prominence.Detail

                column key label prominence false (fun row ->
                    match CaseState.fields row.State with
                    | None -> PresentedValue.Absent "lifecycle.ERASED.label"
                    | Some fields ->
                        match Map.tryFind role fields.Parties |> Option.defaultValue [] with
                        | [] -> optional PresentedValue.Text None
                        | names ->
                            PresentedValue.Text(
                                names |> List.map PartyName.format |> String.concat "; "
                            ))
            yield!
                caseFields FieldPresentation.declarations (fun (row: CaseSummary) ->
                    CaseState.fields row.State
                    |> Option.map (fun (facts: CaseSummaryFacts) -> facts.Fields))
                |> List.map (fun definition ->
                    { definition with
                        Prominence =
                            if definition.Key = BusinessField.key BusinessField.PayableAmount then
                                Prominence.Detail
                            else
                                Prominence.Secondary
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
            column "nextDue" "field.reminderDueDate.label" Prominence.Secondary false (fun row ->
                row.NextAction |> Option.map _.Due |> optional PresentedValue.Date)
            {
                Key = "changedAt"
                LabelId = "label.changedAt"
                Prominence = Prominence.Detail
                IsMoney = false
                Read = fun row -> PresentedValue.Instant row.ChangedAt
            }
        ]

    let caseSummary row =
        [
            {
                TitleId = "subject.CASE.label"
                EmptyMessageId = None
                Entries = entries caseSummaryColumns row
            }
        ]

    let private resultRows emptyId rows =
        let attention =
            column "attention" "label.attention" Prominence.Primary false (fun (row: CaseSummary) ->
                codes CaseAttention.vocabulary row.Attention)

        let visible, details =
            caseSummaryColumns
            |> List.partition (fun column -> column.Prominence <> Prominence.Detail)

        let data =
            tableWithControls
                (fun (row: CaseSummary) -> CaseId.format row.CaseId)
                (fun _ -> None)
                (visible @ [ attention ] @ details)
                (fun row ->
                    let actions =
                        if row.CanAssign then
                            let seeds =
                                row.Responsible
                                |> Option.map (fun actor ->
                                    Map.ofList
                                        [
                                            TaskAffordances.field BusinessField.ResponsibleActor,
                                            TaskAffordances.text (ActorId.format actor.ActorId)
                                        ])
                                |> Option.defaultValue Map.empty

                            [
                                TaskAffordances.work
                                    row.CaseId
                                    row.WorkRevision
                                    seeds
                                    [ TaskAffordances.field BusinessField.ResponsibleActor ]
                            ]
                        else
                            []

                    Some(Link.caseById row.CaseId), actions)
                rows

        let columns =
            data.Columns
            |> List.map (fun column ->
                { column with
                    Joins =
                        match column.Key with
                        | "attention" -> Some "headline"
                        | "nextDue" -> Some "nextAction"
                        | _ -> None
                })

        Screen.create
            [
                region RegionRole.Results None (RegionContent.Table { data with Columns = columns })
                |> withEmpty emptyId
            ]

    let rows items = resultRows "label.noCasesForView" items

    let view (reply: CaseSummaryPage) =
        let screen =
            if reply.AuthorizedScopeEmpty then
                resultRows "label.noCasesInScope" reply.Items
            else
                rows reply.Items

        let screen =
            match reply.ExactReference with
            | None -> screen
            | Some exact ->
                let facts =
                    [
                        value
                            "exactReference"
                            "field.caseReference.label"
                            Prominence.Primary
                            (PresentedValue.Reference exact.CaseReference)
                    ]

                Screen.withRegions
                    (screen.Regions
                     @ [ entryRegion RegionRole.Facts (Some "label.exactReference") facts ])
                    screen

        screen

    let document (value: CaseSummaryPage) =
        let links =
            value.ExactReference
            |> Option.map (fun value ->
                NamedLink.create
                    "field.caseReference.label"
                    LinkRelation.OpenCase
                    (Link.caseById value.CaseId))
            |> Option.toList

        HumanDocument.snapshot
            value.Basis
            (view value)
            DocumentSubject.List
            (value.Offers |> List.map (TaskAffordances.installationOffer None))
            links
            value.NextCursor
            HumanDisclosure.Current
            None
