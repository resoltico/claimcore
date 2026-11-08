namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Presentation

module internal ViewText =
    let private message language (message: PresentedMessage) =
        message.Arguments
        |> Map.map (fun _ value -> TextDescriptorValue.render language value)
        |> MessageCatalogs.render language message.MessageId

    let private renderWithHeader includeHeader includeDetails options language (screen: Screen) =
        let text = TextDescriptorValue.render language
        let sections = TextDescriptorValue.sections language
        let label = ReplyTextFormat.label language

        let detailLines =
            function
            | None -> []
            | Some(detail: RowDetail) ->
                (detail.Summary |> Option.map (message language) |> Option.toList)
                @ sections detail.Highlights
                @ sections detail.Sections

        let table (table: RegionTable) =
            let definitions =
                table.Columns
                |> List.mapi (fun index column ->
                    {
                        EntryDefinition.Key = column.Key
                        LabelId = column.LabelId
                        Prominence = column.Prominence
                        IsMoney = column.IsMoney
                        Read = fun row -> List.item index row
                    })

            let header, render =
                TextTable.page language options definitions (table.Rows |> List.map _.Values)

            let rows values =
                values
                |> List.collect (fun row -> render [ row.Values ] @ detailLines row.Detail)

            (if includeHeader && header <> "" then [ header ] else [])
            @ (if table.Groups.IsEmpty then
                   rows table.Rows
               else
                   table.Groups
                   |> List.collect (fun group ->
                       let selected =
                           group.RowKeys
                           |> List.map (fun key ->
                               table.Rows |> List.find (fun row -> row.Key = key))

                       [ label group.TitleId + ": " + text (PresentedValue.Count group.Count) ]
                       @ rows selected))
            @ sections table.Sections

        let render region =
            let heading =
                if includeHeader then
                    region.TitleId |> Option.map label |> Option.toList
                else
                    []

            let body =
                match region.Content with
                | RegionContent.Entries entries ->
                    if includeHeader || (includeDetails && region.Role = RegionRole.Details) then
                        entries
                        |> List.map (fun entry -> label entry.LabelId + ": " + text entry.Value)
                    else
                        []
                | RegionContent.Table data -> table data
                | RegionContent.Stages data ->
                    data.Steps
                    |> List.map (fun step ->
                        let title =
                            Vocabulary.messageKey CaseStageKind.vocabulary step.Stage "label"
                            |> label

                        let date =
                            step.Date
                            |> Option.map PresentedValue.Date
                            |> Option.map text
                            |> Option.defaultValue (label "label.notRecorded")

                        let amount =
                            step.Amount
                            |> Option.map PresentedValue.Money
                            |> Option.map text
                            |> Option.toList

                        (if step.Reached then "[x] " else "[ ] ")
                        + title
                        + ": "
                        + String.concat " · " (date :: amount))
                | RegionContent.Codes(vocabulary, tokens) ->
                    if tokens.IsEmpty then
                        []
                    else
                        [
                            tokens
                            |> List.map (fun token -> text (PresentedValue.Code(vocabulary, token)))
                            |> String.concat ", "
                        ]
                | RegionContent.Actions actions ->
                    (actions.Recommended
                     |> Option.map (fun act ->
                         label "label.recommendedNext"
                         + ": "
                         + text (
                             PresentedValue.Code(
                                 Vocabulary.name ActKind.vocabulary,
                                 ActKind.token act
                             )
                         ))
                     |> Option.toList)
                    @ (actions.Groups
                       |> List.map (fun group ->
                           label (Vocabulary.messageKey ActFamily.vocabulary group.Family "label")
                           + ": "
                           + (group.Acts
                              |> List.map (fun act ->
                                  text (
                                      PresentedValue.Code(
                                          Vocabulary.name ActKind.vocabulary,
                                          ActKind.token act
                                      )
                                  ))
                              |> String.concat ", ")))
                | RegionContent.Notice notice ->
                    [ label notice.MessageId ] @ (notice.Reason |> Option.map text |> Option.toList)
                | RegionContent.Timeline entries ->
                    entries
                    |> List.collect (fun entry ->
                        [
                            message language entry.Summary
                            text (PresentedValue.Actor entry.Who)
                            + " · "
                            + text (PresentedValue.Instant entry.When)
                        ]
                        @ sections entry.Highlights
                        @ sections entry.Details)

            if body.IsEmpty then [] else heading @ body

        let lines =
            screen.Regions
            |> List.filter (fun region -> includeDetails || region.Role <> RegionRole.Details)
            |> List.collect render

        if lines.IsEmpty && includeHeader then
            [ label "view.empty" ]
        else
            lines

    let renderWithDetails includeDetails options language screen =
        renderWithHeader true includeDetails options language screen

    let renderRowsWithDetails includeDetails options language screen =
        renderWithHeader false includeDetails options language screen

    let renderWith options language screen =
        renderWithDetails false options language screen

    let renderRowsWith options language screen =
        renderRowsWithDetails false options language screen

    let renderSelected includeDetails language screen =
        renderWithDetails includeDetails (TextTable.environment ()) language screen

    let render language screen = renderSelected false language screen
