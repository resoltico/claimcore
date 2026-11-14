namespace ClaimCore.Cli

open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation

module internal ViewText =
    let private message language (message: PresentedMessage) =
        message.Arguments
        |> Map.map (fun _ value -> TextDescriptorValue.render language value)
        |> MessageCatalogs.render language message.MessageId

    let private renderWithHeader
        timeZone
        includeHeader
        includeDetails
        options
        language
        (screen: Screen)
        =
        let text = TextDescriptorValue.renderIn timeZone language
        let sections = TextDescriptorValue.sectionsIn timeZone language
        let label = ReplyTextFormat.label language

        let detailLines =
            function
            | None -> []
            | Some(detail: RowDetail) ->
                (detail.Summary |> Option.map (message language) |> Option.toList)
                @ sections detail.Highlights
                @ (if includeDetails then sections detail.Sections else [])

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
                TextTable.pageIn
                    timeZone
                    language
                    options
                    definitions
                    (table.Rows |> List.map _.Values)

            let rows values =
                values
                |> List.collect (fun row -> render [ row.Values ] @ detailLines row.Detail)

            (if table.Meter.IsSome then sections table.Sections else [])
            @ (table.Meter
               |> Option.map (fun meter -> message language meter.Description)
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
                            match step.Meter with
                            | Some meter -> [ message language meter.Description ]
                            | None ->
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
                        @ (if includeDetails then sections entry.Details else []))

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
        renderWithHeader "UTC" true includeDetails options language screen

    let renderRowsWithDetails includeDetails options language screen =
        renderWithHeader "UTC" false includeDetails options language screen

    let renderWith options language screen =
        renderWithDetails false options language screen

    let renderRowsWith options language screen =
        renderRowsWithDetails false options language screen

    let renderSelected includeDetails language screen =
        renderWithDetails includeDetails (TextTable.environment ()) language screen

    /// Renders instants in the supplied installation zone instead of UTC.
    let renderSelectedIn timeZone includeDetails language screen =
        renderWithHeader timeZone true includeDetails (TextTable.environment ()) language screen

    let render language screen = renderSelected false language screen

    let renderDocument includeDetails language (document: HumanDocument) =
        let zone =
            match document.Basis with
            | ObservationBasis.Snapshot basis -> basis.TimeZoneId
            | ObservationBasis.Unavailable -> "UTC"

        let body =
            renderWithHeader
                zone
                true
                includeDetails
                (TextTable.environment ())
                language
                document.Screen

        let actions =
            document.Actions
            |> List.filter (fun action -> action.Blocked.IsNone)
            |> List.map (fun action ->
                let key =
                    match action.Task with
                    | TaskId.Operation task ->
                        Vocabulary.messageKey ActKind.vocabulary task "label"
                    | TaskId.Command command ->
                        Vocabulary.messageKey
                            ActorEndpoint.vocabulary
                            (ActorEndpoint.Command command)
                            "label"
                    | TaskId.Query query ->
                        Vocabulary.messageKey
                            ActorEndpoint.vocabulary
                            (ActorEndpoint.Query query)
                            "label"

                ReplyTextFormat.label language key)

        body
        @ (if actions.IsEmpty then
               []
           else
               [ String.concat ", " actions ])
