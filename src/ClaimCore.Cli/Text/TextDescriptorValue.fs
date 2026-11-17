namespace ClaimCore.Cli

open System
open System.Globalization
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Presentation

/// Text escaping and locale formatting are interface responsibilities; every value kind is explicit.
module internal TextDescriptorValue =
    let private code (rendering: RenderingContext) vocabulary token =
        let key = PresentationKeys.code vocabulary token

        if vocabulary = "country" then
            ReplyTextFormat.country rendering token
            |> Some
            |> ReplyTextFormat.displayed rendering
        elif vocabulary = "currency" then
            ReplyTextFormat.displayed rendering (Some token)
        else
            ReplyTextFormat.label rendering key

    let private codes (rendering: RenderingContext) vocabulary =
        function
        | [] -> "—"
        | tokens -> tokens |> List.map (code rendering vocabulary) |> String.concat ", "

    let private text (rendering: RenderingContext) value =
        ReplyTextFormat.displayed rendering (Some value)

    // This exhaustive semantic-value table keeps every renderer case visible without a fallback.
    let renderIn timeZone (rendering: RenderingContext) =
        function
        | PresentedValue.Money value ->
            ReplyTextFormat.moneyWithScale rendering value.Value value.DisplayScale
        | PresentedValue.MoneyTotal value ->
            ReplyTextFormat.moneyTotalWithScale
                rendering
                value.Currency
                value.Value
                value.DisplayScale
        | PresentedValue.Date value -> ReplyTextFormat.date rendering value |> text rendering
        | PresentedValue.Instant value ->
            ReplyTextFormat.instantIn rendering timeZone value |> text rendering
        | PresentedValue.Party value -> PartyName.format value |> text rendering
        | PresentedValue.Text value -> text rendering value
        | PresentedValue.Narrative value -> ReplyTextFormat.narrative rendering (Some value)
        | PresentedValue.Message key -> ReplyTextFormat.label rendering key
        | PresentedValue.Code(vocabulary, token) -> code rendering vocabulary token
        | PresentedValue.Codes(vocabulary, tokens) -> codes rendering vocabulary tokens
        | PresentedValue.Actor value -> ReplyTextFormat.actorName rendering value |> text rendering
        | PresentedValue.State value -> ReplyTextFormat.term rendering CaseHeadline.vocabulary value
        | PresentedValue.Count value -> ReplyTextFormat.number rendering value
        | PresentedValue.Reference value -> CaseReference.format value |> text rendering
        | PresentedValue.Identifier value -> text rendering value
        | PresentedValue.Absent reason -> ReplyTextFormat.label rendering reason

    let render (rendering: RenderingContext) value = renderIn "UTC" rendering value

    /// Tables keep admitted paragraph breaks visible as data inside a single cell.
    let cellIn timeZone (rendering: RenderingContext) =
        function
        | PresentedValue.Narrative value -> ReplyTextFormat.displayed rendering (Some value)
        | value -> renderIn timeZone rendering value

    let cell (rendering: RenderingContext) value = cellIn "UTC" rendering value

    let entry (rendering: RenderingContext) (entry: Entry) =
        ReplyTextFormat.line rendering entry.LabelId (render rendering entry.Value)

    let entriesIn timeZone (rendering: RenderingContext) (values: Entry list) =
        let labels =
            values |> List.map (fun entry -> ReplyTextFormat.label rendering entry.LabelId)

        let width value = StringInfo(value).LengthInTextElements
        let maximum = labels |> List.map width |> List.fold max 0

        List.map2
            (fun label entry ->
                label
                + String(' ', maximum - width label)
                + ": "
                + renderIn timeZone rendering entry.Value)
            labels
            values

    let entries (rendering: RenderingContext) values = entriesIn "UTC" rendering values

    let sectionsIn timeZone (rendering: RenderingContext) values =
        values
        |> List.filter (fun (section: Section) -> not section.Entries.IsEmpty)
        |> List.collect (fun (section: Section) ->
            (if section.Entries |> List.exists (fun entry -> entry.LabelId = section.TitleId) then
                 []
             else
                 [ ReplyTextFormat.label rendering section.TitleId ])
            @ entriesIn timeZone rendering section.Entries)

    let sections (rendering: RenderingContext) values = sectionsIn "UTC" rendering values
