namespace ClaimCore.Cli

open System.Globalization
open ClaimCore.Domain
open ClaimCore.Protocol
open ClaimCore.Protocol.Presentation

/// Text escaping and locale formatting are interface responsibilities; every value kind is explicit.
module internal TextDescriptorValue =
    let render language =
        let text value =
            TextReplyFormat.displayed language (Some value)

        function
        | PresentedValue.Money value -> TextReplyFormat.money language value
        | PresentedValue.Date value -> CalendarDate.format value |> text
        | PresentedValue.Instant value -> TextReplyFormat.instant value |> text
        | PresentedValue.Party value -> PartyName.format value |> text
        | PresentedValue.Text value -> text value
        | PresentedValue.Code(vocabulary, token) ->
            let key = vocabulary + "." + token + ".label"

            if MessageCatalogs.templates language |> Map.containsKey key then
                TextReplyFormat.label language key
            else
                text token
        | PresentedValue.Actor value -> TextReplyFormat.actorName language value |> text
        | PresentedValue.State value -> TextReplyFormat.term language CaseHeadline.vocabulary value
        | PresentedValue.Count value -> value.ToString(CultureInfo.InvariantCulture)
        | PresentedValue.Reference value -> CaseReference.format value |> text
        | PresentedValue.Identifier value -> text value
        | PresentedValue.Absent reason -> TextReplyFormat.label language reason

    let entry language (entry: Entry) =
        TextReplyFormat.line language entry.LabelId (render language entry.Value)

    let entries language values = List.map (entry language) values
