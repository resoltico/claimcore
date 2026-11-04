namespace ClaimCore.Cli

open System
open System.Globalization
open ClaimCore.Domain
open ClaimCore.Protocol
open ClaimCore.Protocol.Presentation

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

    let render language =
        let text value =
            TextReplyFormat.displayed language (Some value)

        function
        | PresentedValue.Money value -> TextReplyFormat.money language value
        | PresentedValue.Date value -> TextReplyFormat.date language value |> text
        | PresentedValue.Instant value -> TextReplyFormat.instant value |> text
        | PresentedValue.Party value -> PartyName.format value |> text
        | PresentedValue.Text value -> text value
        | PresentedValue.Code(vocabulary, token) -> code language vocabulary token
        | PresentedValue.Actor value -> TextReplyFormat.actorName language value |> text
        | PresentedValue.State value -> TextReplyFormat.term language CaseHeadline.vocabulary value
        | PresentedValue.Count value -> value.ToString(CultureInfo.InvariantCulture)
        | PresentedValue.Reference value -> CaseReference.format value |> text
        | PresentedValue.Identifier value -> text value
        | PresentedValue.Absent reason -> TextReplyFormat.label language reason

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
