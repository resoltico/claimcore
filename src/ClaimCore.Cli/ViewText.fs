namespace ClaimCore.Cli

open ClaimCore.Presentation

module internal ViewText =
    let private renderWithHeader includeHeader options language =
        function
        | View.Record sections ->
            match TextDescriptorValue.sections language sections with
            | [] -> [ ReplyTextFormat.label language "view.empty" ]
            | lines -> lines
        | View.Table(columns, rows) ->
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
            (if header = "" || not includeHeader then [] else [ header ]) @ render rows

    let renderWith options language view =
        renderWithHeader true options language view

    let renderRowsWith options language view =
        renderWithHeader false options language view

    let render language view =
        renderWith (TextTable.environment ()) language view
