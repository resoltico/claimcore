namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation

module internal HumanText =
    let private message (rendering: RenderingContext) (value: PresentedMessage) =
        value.Arguments
        |> Map.map (fun _ value -> TextDescriptorValue.render rendering value)
        |> MessageCatalogs.render rendering.Language value.MessageId

    let advice (rendering: RenderingContext) (value: HumanAdvice) =
        [ message rendering value.Message ]

    let fault (rendering: RenderingContext) value =
        [
            MessageCatalogs.render
                rendering.Language
                (DiagnosticCode.key (CoreFault.token value))
                Map.empty
        ]

    let private issue (rendering: RenderingContext) (value: HumanInputIssue) =
        let label =
            TaskInputPresentation.messageKey value.Input "label"
            |> ReplyTextFormat.label rendering

        let related =
            value.Related
            |> Option.map (fun entry ->
                " ("
                + ReplyTextFormat.label rendering entry.LabelId
                + ": "
                + TextDescriptorValue.render rendering entry.Value
                + ")")
            |> Option.defaultValue ""

        label + ": " + message rendering value.Message + related

    let rejection details (rendering: RenderingContext) =
        function
        | HumanRejection.Input(value, issues) ->
            advice rendering value @ (issues |> List.map (issue rendering))
        | HumanRejection.Conflict(value, _, document) ->
            advice rendering value @ ViewText.renderDocument details rendering document
        | HumanRejection.Refused value -> advice rendering value

    let read details (rendering: RenderingContext) (reply: HumanReadReply) =
        match reply.Outcome with
        | HumanReadOutcome.Succeeded document -> ViewText.renderDocument details rendering document
        | HumanReadOutcome.Failed value -> fault rendering value
        | HumanReadOutcome.Rejected value -> rejection details rendering value
