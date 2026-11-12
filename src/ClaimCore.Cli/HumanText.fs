namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation

module internal HumanText =
    let private message language (value: PresentedMessage) =
        value.Arguments
        |> Map.map (fun _ value -> TextDescriptorValue.render language value)
        |> MessageCatalogs.render language value.MessageId

    let advice language (value: HumanAdvice) = [ message language value.Message ]

    let private issue language (value: HumanInputIssue) =
        let label =
            TaskInputPresentation.messageKey value.Input "label"
            |> ReplyTextFormat.label language

        let related =
            value.Related
            |> Option.map (fun entry ->
                " ("
                + ReplyTextFormat.label language entry.LabelId
                + ": "
                + TextDescriptorValue.render language entry.Value
                + ")")
            |> Option.defaultValue ""

        label + ": " + message language value.Message + related

    let rejection details language =
        function
        | HumanRejection.Input(value, issues) ->
            advice language value @ (issues |> List.map (issue language))
        | HumanRejection.Conflict(value, _, document) ->
            advice language value @ ViewText.renderSelected details language document.Screen
        | HumanRejection.Refused value -> advice language value

    let read details language (reply: HumanReadReply) =
        match reply.Outcome with
        | HumanReadOutcome.Succeeded document ->
            ViewText.renderSelected details language document.Screen
        | HumanReadOutcome.Failed value -> advice language value
        | HumanReadOutcome.Rejected value -> rejection details language value
