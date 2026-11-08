namespace ClaimCore.Cli

open ClaimCore.Presentation

module internal ViewText =
    let private renderWithHeader includeHeader options language =
        function
        | View.Record sections ->
            match TextDescriptorValue.sections language sections with
            | [] -> [ ReplyTextFormat.label language "view.empty" ]
            | lines -> lines
        | View.Table(columns, rows, details, sections) ->
            let definitions =
                columns
                |> List.mapi (fun index column ->
                    {
                        EntryDefinition.Key = column.Key
                        LabelId = column.LabelId
                        Prominence = column.Prominence
                        IsMoney = column.IsMoney
                        Read = fun row -> List.item index row
                    })

            let header, render = TextTable.page language options definitions rows

            let detailLines =
                function
                | None -> []
                | Some(detail: RowDetail) ->
                    let summary =
                        detail.Summary
                        |> Option.map (fun message ->
                            message.Arguments
                            |> Map.map (fun _ value -> TextDescriptorValue.render language value)
                            |> MessageCatalogs.render language message.MessageId)
                        |> Option.toList

                    summary
                    @ TextDescriptorValue.sections language detail.Highlights
                    @ TextDescriptorValue.sections language detail.Sections

            (if includeHeader then
                 TextDescriptorValue.sections language sections
             else
                 [])
            @ (if header = "" || not includeHeader then [] else [ header ])
            @ (List.map2 (fun row detail -> render [ row ] @ detailLines detail) rows details
               |> List.concat)

    let renderWith options language view =
        renderWithHeader true options language view

    let renderRowsWith options language view =
        renderWithHeader false options language view

    let render language view =
        renderWith (TextTable.environment ()) language view
