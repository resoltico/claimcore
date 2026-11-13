namespace ClaimCore.Cli

open System
open System.Globalization
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Presentation

/// Text escaping and locale formatting are interface responsibilities; every value kind is explicit.
module internal TextDescriptorValue =
    let private code language vocabulary token =
        let key = PresentationKeys.code vocabulary token

        if vocabulary = "country" then
            ReplyTextFormat.country language token
            |> Some
            |> ReplyTextFormat.displayed language
        elif vocabulary = "currency" then
            ReplyTextFormat.displayed language (Some token)
        else
            ReplyTextFormat.label language key

    let private codes language vocabulary =
        function
        | [] -> "—"
        | tokens -> tokens |> List.map (code language vocabulary) |> String.concat ", "

    let private text language value =
        ReplyTextFormat.displayed language (Some value)

    // This exhaustive semantic-value table keeps every renderer case visible without a fallback.
    let renderIn timeZone language =
        function
        | PresentedValue.Money value ->
            ReplyTextFormat.moneyWithScale language value.Value value.DisplayScale
        | PresentedValue.MoneyTotal value ->
            ReplyTextFormat.moneyTotalWithScale
                language
                value.Currency
                value.Value
                value.DisplayScale
        | PresentedValue.Date value -> ReplyTextFormat.date language value |> text language
        | PresentedValue.Instant value ->
            ReplyTextFormat.instantIn language timeZone value |> text language
        | PresentedValue.Party value -> PartyName.format value |> text language
        | PresentedValue.Text value -> text language value
        | PresentedValue.Narrative value -> ReplyTextFormat.narrative language (Some value)
        | PresentedValue.Message key -> ReplyTextFormat.label language key
        | PresentedValue.Code(vocabulary, token) -> code language vocabulary token
        | PresentedValue.Codes(vocabulary, tokens) -> codes language vocabulary tokens
        | PresentedValue.Actor value -> ReplyTextFormat.actorName language value |> text language
        | PresentedValue.State value -> ReplyTextFormat.term language CaseHeadline.vocabulary value
        | PresentedValue.Count value -> value.ToString(CultureInfo.InvariantCulture)
        | PresentedValue.Reference value -> CaseReference.format value |> text language
        | PresentedValue.Identifier value -> text language value
        | PresentedValue.Absent reason -> ReplyTextFormat.label language reason

    let render language value = renderIn "UTC" language value

    /// Tables keep admitted paragraph breaks visible as data inside a single cell.
    let cellIn timeZone language =
        function
        | PresentedValue.Narrative value -> ReplyTextFormat.displayed language (Some value)
        | value -> renderIn timeZone language value

    let cell language value = cellIn "UTC" language value

    let entry language (entry: Entry) =
        ReplyTextFormat.line language entry.LabelId (render language entry.Value)

    let entriesIn timeZone language (values: Entry list) =
        let labels =
            values |> List.map (fun entry -> ReplyTextFormat.label language entry.LabelId)

        let width value = StringInfo(value).LengthInTextElements
        let maximum = labels |> List.map width |> List.fold max 0

        List.map2
            (fun label entry ->
                label
                + String(' ', maximum - width label)
                + ": "
                + renderIn timeZone language entry.Value)
            labels
            values

    let entries language values = entriesIn "UTC" language values

    let sectionsIn timeZone language values =
        values
        |> List.filter (fun (section: Section) -> not section.Entries.IsEmpty)
        |> List.collect (fun (section: Section) ->
            (if section.Entries |> List.exists (fun entry -> entry.LabelId = section.TitleId) then
                 []
             else
                 [ ReplyTextFormat.label language section.TitleId ])
            @ entriesIn timeZone language section.Entries)

    let sections language values = sectionsIn "UTC" language values
