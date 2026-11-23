namespace ClaimCore.Cli

open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation

module internal ViewText =
    let private message (rendering: RenderingContext) (message: PresentedMessage) =
        message.Arguments
        |> Map.map (fun _ value -> TextDescriptorValue.render rendering value)
        |> MessageCatalogs.render rendering.Language message.MessageId

    let private renderWithHeader
        timeZone
        showEmpty
        includeHeader
        includeDetails
        options
        rendering
        (screen: Screen)
        =
        let text = TextDescriptorValue.renderIn timeZone rendering
        let sections = TextDescriptorValue.sectionsIn timeZone rendering
        let label = ReplyTextFormat.label rendering

        let detailLines =
            function
            | None -> []
            | Some(detail: RowDetail) ->
                (detail.Summary |> Option.map (message rendering) |> Option.toList)
                @ sections detail.Highlights
                @ (if includeDetails then sections detail.Sections else [])

        let table related (table: RegionTable) =
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
                TextTable.pageIn
                    timeZone
                    rendering
                    options
                    definitions
                    (table.Rows |> List.map _.Values)

            let rows values =
                values
                |> List.collect (fun row ->
                    render [ row.Values ]
                    @ detailLines row.Detail
                    @ [
                        if includeDetails && related then
                            for link in Option.toList row.Link do
                                label (CliMessages.key CliMessage.NextLabel)
                                + ": "
                                + ControlHints.link link
                    ])

            (if table.Meter.IsSome then sections table.Sections else [])
            @ (table.Meter
               |> Option.map (fun meter -> message rendering meter.Description)
               |> Option.toList)
            @ (if includeHeader && header <> "" then [ header ] else [])
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
            @ (if table.Meter.IsNone then sections table.Sections else [])

        let render region =
            let heading =
                if includeHeader then
                    region.TitleId
                    |> Option.filter (fun title ->
                        match region.Content with
                        | RegionContent.Table table ->
                            table.Sections
                            |> List.collect _.Entries
                            |> List.exists (fun entry -> label entry.LabelId = label title)
                            |> not
                        | _ -> true)
                    |> Option.map label
                    |> Option.toList
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
                | RegionContent.Table data when
                    region.EmptyId.IsSome
                    && data.Rows.IsEmpty
                    && data.Sections.IsEmpty
                    && data.Meter.IsNone
                    ->
                    []
                | RegionContent.Table data -> table (region.Role = RegionRole.RelatedCases) data
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
                            match step.Meter with
                            | Some meter -> [ message rendering meter.Description ]
                            | None -> step.StageValue |> Option.map text |> Option.toList

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
                | RegionContent.Notice notices ->
                    notices
                    |> List.collect (fun notice ->
                        [ label notice.MessageId ]
                        @ (notice.Reason |> Option.map text |> Option.toList))
                | RegionContent.Timeline entries ->
                    entries
                    |> List.collect (fun entry ->
                        [
                            message rendering entry.Summary
                            yield! entry.Target |> Option.map text |> Option.toList
                            text entry.Who + " · " + text (PresentedValue.Instant entry.When)
                        ]
                        @ sections entry.Highlights
                        @ (if entry.ChangedFields.IsEmpty then
                               []
                           else
                               [ entry.ChangedFields |> List.map label |> String.concat " · " ])
                        @ (if includeDetails then
                               (entry.Changes
                                |> List.collect (fun change ->
                                    [
                                        label change.LabelId
                                        label "label.before" + ": " + text change.Before
                                        label "label.after" + ": " + text change.After
                                    ]))
                               @ sections entry.Details
                           else
                               []))

            if body.IsEmpty then
                if includeHeader && showEmpty then
                    region.EmptyId |> Option.map label |> Option.toList
                else
                    []
            else
                heading @ body

        screen.Regions
        |> List.filter (fun region -> includeDetails || region.Role <> RegionRole.Details)
        |> List.collect render

    let renderWithDetails includeDetails options (rendering: RenderingContext) screen =
        renderWithHeader "UTC" true true includeDetails options rendering screen

    let renderRowsWithDetails includeDetails options (rendering: RenderingContext) screen =
        renderWithHeader "UTC" true false includeDetails options rendering screen

    let renderWith options (rendering: RenderingContext) screen =
        renderWithDetails false options rendering screen

    let renderRowsWith options (rendering: RenderingContext) screen =
        renderRowsWithDetails false options rendering screen

    let renderSelected includeDetails (rendering: RenderingContext) screen =
        renderWithDetails includeDetails (TextTable.environment ()) rendering screen

    /// Renders instants in the supplied installation zone instead of UTC.
    let renderSelectedIn timeZone includeDetails (rendering: RenderingContext) screen =
        renderWithHeader
            timeZone
            true
            true
            includeDetails
            (TextTable.environment ())
            rendering
            screen

    let render (rendering: RenderingContext) screen = renderSelected false rendering screen

    let private renderDocumentCore
        showEmpty
        options
        includeHeader
        includeDetails
        rendering
        (document: HumanDocument)
        =
        let zone =
            match document.Basis with
            | ObservationBasis.Snapshot basis -> basis.TimeZoneId
            | ObservationBasis.Unavailable -> "UTC"

        let screen =
            document.Screen.Regions
            |> List.filter (fun region -> region.Role <> RegionRole.Context)
            |> fun regions -> Screen.withRegions regions document.Screen

        let body =
            renderWithHeader zone showEmpty includeHeader includeDetails options rendering screen

        let actions =
            Screen.affordances screen @ document.Actions
            |> List.collect (ControlHints.action rendering)

        let links =
            (document.Links |> List.map (fun value -> value.LabelId, value.Link))
            @ (screen.Regions
               |> List.collect (fun region ->
                   let children =
                       match region.Content with
                       | RegionContent.Table table when
                           not (includeDetails && region.Role = RegionRole.RelatedCases)
                           ->
                           table.Rows |> List.choose _.Link
                       | RegionContent.Timeline entries -> entries |> List.choose _.Link
                       | RegionContent.Stages stages -> stages.Steps |> List.choose _.Link
                       | _ -> []

                   (region.Link |> Option.toList) @ children
                   |> List.map (fun link -> CliMessages.key CliMessage.NextLabel, link)))
            |> List.distinct
            |> List.map (fun (label, link) ->
                ReplyTextFormat.label rendering label + ": " + ControlHints.link link)

        body @ actions @ links

    let renderDocumentWithHeader options includeHeader includeDetails rendering document =
        renderDocumentCore true options includeHeader includeDetails rendering document

    let renderActionableRefusal includeDetails rendering document =
        renderDocumentCore false (TextTable.environment ()) true includeDetails rendering document

    let renderDocument includeDetails (rendering: RenderingContext) document =
        renderDocumentWithHeader (TextTable.environment ()) true includeDetails rendering document
