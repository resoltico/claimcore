namespace ClaimCore.Cli

open System
open System.Globalization
open ClaimCore.Domain
open ClaimCore.Presentation

[<NoEquality; NoComparison>]
type internal TextTableOptions = { Width: int; BoldHeader: bool }

/// Layout consumes semantic prominence and value kinds; escaping happens before atomic truncation.
module internal TextTable =
    let private atoms (text: string) =
        let rec collect index values =
            if index >= text.Length then
                List.rev values
            else
                let size =
                    if text[index] = '\\' && index + 1 < text.Length && text[index + 1] = '\\' then
                        2
                    elif
                        text[index] = '\\'
                        && text.Substring(index).StartsWith("\\u{", StringComparison.Ordinal)
                    then
                        let finish = text.IndexOf('}', index + 3)
                        if finish >= 0 then finish - index + 1 else 1
                    else
                        StringInfo.GetNextTextElement(text, index).Length

                let atom = text.Substring(index, size)
                collect (index + size) (atom :: values)

        collect 0 []

    let private atomWidth (atom: string) =
        if atom.StartsWith("\\", StringComparison.Ordinal) then
            atom.Length
        else
            let cells = Wcwidth.UnicodeCalculator.GetWidth(atom)

            if cells < 0 then
                invalidOp "Terminal data must be escaped before table layout."

            cells

    let width text = atoms text |> List.sumBy atomWidth

    let truncate maximum text =
        if width text <= maximum then
            text
        elif maximum <= 0 then
            ""
        else
            let rec retain budget =
                function
                | atom :: rest when atomWidth atom <= budget ->
                    atom :: retain (budget - atomWidth atom) rest
                | _ -> []

            String.concat "" (retain (maximum - 1) (atoms text)) + "…"

    let environment () =
        let terminalWidth () =
            try
                if Console.IsOutputRedirected then
                    100
                else
                    max 1 Console.WindowWidth
            with :? IO.IOException ->
                100

        let width =
            match Int32.TryParse(Environment.GetEnvironmentVariable "COLUMNS") with
            | true, value when value > 0 -> min 4096 value
            | _ -> terminalWidth ()

        {
            Width = width
            BoldHeader =
                not Console.IsOutputRedirected
                && isNull (Environment.GetEnvironmentVariable "NO_COLOR")
        }

    let private moneyWidth =
        Money.MaximumIntegerDigits
        + (Money.MaximumIntegerDigits - 1) / 3
        + 1
        + Money.MaximumFractionalDigits
        + 1
        + Money.CurrencyCharacters

    let private layout fixedWidths language options (definitions: EntryDefinition<'row> list) =
        let columns =
            definitions
            |> List.map (fun definition ->
                definition, ReplyTextFormat.label language definition.LabelId)

        let minimumWidth (definition: EntryDefinition<'row>) =
            if definition.IsMoney then
                moneyWidth
            else
                Map.tryFind definition.Key fixedWidths |> Option.defaultValue 4

        let desiredWidth (definition: EntryDefinition<'row>) label =
            if definition.IsMoney then
                moneyWidth
            else
                max (minimumWidth definition) (max 8 (min 24 (width label)))

        let desired columns =
            columns
            |> List.sumBy (fun (definition, label) -> desiredWidth definition label)
            |> fun size -> size + max 0 (columns.Length - 1) * 2

        let rec select columns =
            if desired columns <= options.Width then
                columns
            else
                let removable =
                    [ Prominence.Detail; Prominence.Secondary ]
                    |> List.tryPick (fun prominence ->
                        columns
                        |> List.tryFindIndex (fun (definition, _) ->
                            definition.Prominence = prominence))

                match removable with
                | Some index ->
                    columns
                    |> List.indexed
                    |> List.choose (fun (position, column) ->
                        if index = position then None else Some column)
                    |> select
                | None -> columns

        let columns = select columns

        let minimum = columns |> List.sumBy (fun (definition, _) -> minimumWidth definition)

        if minimum + max 0 (columns.Length - 1) * 2 > options.Width then
            None
        else
            let available = options.Width - max 0 (columns.Length - 1) * 2

            let initial =
                columns
                |> List.map (fun (definition, label) ->
                    definition, label, desiredWidth definition label)

            let rec shrink columns =
                let total = columns |> List.sumBy (fun (_, _, size) -> size)

                if total <= available then
                    columns
                else
                    let largest =
                        columns
                        |> List.filter (fun (definition, _, size) -> size > minimumWidth definition)
                        |> List.map (fun (_, _, size) -> size)
                        |> List.max

                    let mutable reduced = false

                    columns
                    |> List.map (fun (definition, label, size) ->
                        if not reduced && size > minimumWidth definition && size = largest then
                            reduced <- true
                            definition, label, size - 1
                        else
                            definition, label, size)
                    |> shrink

            Some(shrink initial)

    let private padded right size text =
        let value = truncate size text
        let padding = String(' ', max 0 (size - width value))
        if right then padding + value else value + padding

    let private headerWith fixedWidths language options definitions =
        match layout fixedWidths language options definitions with
        | None -> ""
        | Some columns ->
            let line =
                columns
                |> List.map (fun (_, label, size) -> padded false size label)
                |> String.concat "  "
                |> _.TrimEnd()

            if options.BoldHeader then
                "\u001b[1m" + line + "\u001b[0m"
            else
                line

    let private rowsWith fixedWidths language options definitions rows =
        match layout fixedWidths language options definitions with
        | None ->
            rows
            |> List.collect (fun row ->
                (definitions
                 |> List.map (fun definition ->
                     {
                         Entry.Key = definition.Key
                         LabelId = definition.LabelId
                         Prominence = definition.Prominence
                         Value = definition.Read row
                     }))
                |> List.collect (fun entry ->
                    [
                        truncate options.Width (ReplyTextFormat.label language entry.LabelId)
                        match entry.Value with
                        | PresentedValue.Money _
                        | PresentedValue.MoneyTotal _ ->
                            TextDescriptorValue.cell language entry.Value
                        | _ ->
                            truncate options.Width (TextDescriptorValue.cell language entry.Value)
                    ]))
        | Some columns ->
            rows
            |> List.map (fun row ->
                columns
                |> List.map (fun (definition, _, size) ->
                    let value = definition.Read row

                    let right =
                        match value with
                        | PresentedValue.Money _
                        | PresentedValue.MoneyTotal _ -> true
                        | _ -> false

                    let rendered = TextDescriptorValue.cell language value

                    if right && width rendered > size then
                        rendered
                    else
                        padded right size rendered)
                |> String.concat "  "
                |> _.TrimEnd())


    let header language options definitions =
        headerWith Map.empty language options definitions

    let rows language options definitions rows =
        rowsWith Map.empty language options definitions rows

    /// Fixed-size semantic values are measured once for the reply, so header and every row share one plan.
    let page language options (definitions: EntryDefinition<'row> list) values =
        let fixedWidths =
            definitions
            |> List.map (fun definition ->
                let minimum =
                    values
                    |> List.map (fun row ->
                        let value = definition.Read row

                        match value with
                        | PresentedValue.Date _
                        | PresentedValue.Instant _
                        | PresentedValue.Count _
                        | PresentedValue.Code _
                        | PresentedValue.Codes _
                        | PresentedValue.Message _
                        | PresentedValue.State _ ->
                            TextDescriptorValue.cell language value |> width
                        | _ -> TextDescriptorValue.cell language value |> width |> min 24 |> max 4)
                    |> List.fold max 4

                definition.Key, minimum)
            |> Map.ofList

        headerWith fixedWidths language options definitions,
        (fun rows -> rowsWith fixedWidths language options definitions rows)
