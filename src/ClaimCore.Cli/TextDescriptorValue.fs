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
        let key = vocabulary + "." + token + ".label"

        if vocabulary = "country" then
            TextReplyFormat.country language token
            |> Some
            |> TextReplyFormat.displayed language
        elif vocabulary = "currency" then
            TextReplyFormat.displayed language (Some token)
        else
            TextReplyFormat.label language key

    let private codes language vocabulary =
        function
        | [] -> "—"
        | tokens -> tokens |> List.map (code language vocabulary) |> String.concat ", "

    let private text language value =
        TextReplyFormat.displayed language (Some value)

    // This exhaustive semantic-value table keeps every renderer case visible without a fallback.
    // fsharplint:disable-next-line CyclomaticComplexity
    let render language =
        function
        | PresentedValue.Money value ->
            TextReplyFormat.moneyWithScale language value.Value value.DisplayScale
        | PresentedValue.MoneyTotal value ->
            TextReplyFormat.moneyTotalWithScale
                language
                value.Currency
                value.Value
                value.DisplayScale
        | PresentedValue.Date value -> TextReplyFormat.date language value |> text language
        | PresentedValue.Instant value -> TextReplyFormat.instant value |> text language
        | PresentedValue.Party value -> PartyName.format value |> text language
        | PresentedValue.Text value -> text language value
        | PresentedValue.Narrative value -> TextReplyFormat.narrative language (Some value)
        | PresentedValue.Message key -> TextReplyFormat.label language key
        | PresentedValue.Code(vocabulary, token) -> code language vocabulary token
        | PresentedValue.Codes(vocabulary, tokens) -> codes language vocabulary tokens
        | PresentedValue.Actor value -> TextReplyFormat.actorName language value |> text language
        | PresentedValue.State value -> TextReplyFormat.term language CaseHeadline.vocabulary value
        | PresentedValue.Count value -> value.ToString(CultureInfo.InvariantCulture)
        | PresentedValue.Reference value -> CaseReference.format value |> text language
        | PresentedValue.Identifier value -> text language value
        | PresentedValue.Absent reason -> TextReplyFormat.label language reason

    /// Tables keep admitted paragraph breaks visible as data inside a single cell.
    let cell language =
        function
        | PresentedValue.Narrative value -> TextReplyFormat.displayed language (Some value)
        | value -> render language value

    let entry language (entry: Entry) =
        TextReplyFormat.line language entry.LabelId (render language entry.Value)

    let entries language (values: Entry list) =
        let labels =
            values |> List.map (fun entry -> TextReplyFormat.label language entry.LabelId)

        let width value = StringInfo(value).LengthInTextElements
        let maximum = labels |> List.map width |> List.fold max 0

        List.map2
            (fun label entry ->
                label + String(' ', maximum - width label) + ": " + render language entry.Value)
            labels
            values

    let sections language values =
        values
        |> List.filter (fun (section: Section) -> not section.Entries.IsEmpty)
        |> List.collect (fun (section: Section) ->
            TextReplyFormat.label language section.TitleId
            :: entries language section.Entries)
