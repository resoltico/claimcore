namespace ClaimCore.Cli

open System
open System.Globalization
open System.Text
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ClaimCore.Json

module internal ReplyText =
    open ReplyTextFormat

    type private Presenter = bool -> string -> Invocation option -> ServiceReply -> string list

    let private presenter (endpoint: Endpoint<'Input, 'Output>) render : EndpointId * Presenter =
        endpoint.Id,
        (fun details language request value ->
            ServiceReply.value endpoint value
            |> Option.defaultWith (fun () -> invalidOp "An admitted reply must remain valid.")
            |> render details language request)

    let private read endpoint render =
        presenter endpoint (fun details language request outcome ->
            DiagnosticText.readLinesFor
                language
                (DiagnosticText.reference request)
                (render details language)
                outcome)

    let private readPresenter endpoint render = read endpoint render |> snd

    let private screenPresenter endpoint =
        readPresenter endpoint (fun details language (reply: Presented<_>) ->
            ViewText.renderSelected details language reply.View)

    let private presenterFor =
        function
        | EndpointId.Actor(ActorEndpoint.Query QueryTaskId.OperationGet) ->
            presenter Api.operationGet (fun details language _ reply ->
                OperationText.getLinesWithDetails details language reply)
            |> snd
        | EndpointId.Actor(ActorEndpoint.Query _ | ActorEndpoint.Command _) ->
            fun details language request (reply: ServiceReply) ->
                match EndpointValues.readReply reply.Typed with
                | Some(RepresentationReply.Human value) -> HumanText.read details language value
                | Some(RepresentationReply.Data outcome) ->
                    DiagnosticText.readLinesFor
                        language
                        (DiagnosticText.reference request)
                        (fun _ -> invalidOp "Human text requires a HUMAN success response.")
                        outcome
                | None -> invalidOp "A query requires an admitted read reply."
        | EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Preview) ->
            presenter Api.operationPreview OperationText.previewLinesWithDetails |> snd
        | EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Execute) ->
            presenter Api.operationExecute OperationText.executeLinesForWithDetails |> snd
        | EndpointId.Host HostEndpoint.Session -> screenPresenter Api.session
        | EndpointId.Host HostEndpoint.Logout -> screenPresenter Api.logout

    let private replyLines details language request (reply: ServiceReply) =
        presenterFor reply.Route.Id details language request reply

    let resultLinesWithDetails details language request =
        function
        | FrameResult.Reply reply -> replyLines details language request reply
        | FrameResult.HostFailure failure ->
            [
                // The shared host messages advise browser actions; the CLI states its own next step.
                match failure.Code with
                | HostFailureCode.ContractMismatch ->
                    MessageCatalogs.renderCli
                        language
                        (CliMessage.Failure ClientFailure.ContractMismatch)
                | HostFailureCode.InstallationMismatch ->
                    MessageCatalogs.renderCli
                        language
                        (CliMessage.Failure ClientFailure.InstallationMismatch)
                | code ->
                    label
                        language
                        (DiagnosticCode.key (Vocabulary.token HostFailureCode.vocabulary code))
                line
                    language
                    (CliMessages.key CliMessage.CodeLabel)
                    (Vocabulary.token HostFailureCode.vocabulary failure.Code)
            ]
        | FrameResult.LocalFailure(CliFailure.ConfigurationIssue(setting, issue)) ->
            let setting = InputDiagnostics.settingName setting

            let issue = ConfigurationIssue.token issue

            [
                MessageCatalogs.renderCli language (CliMessage.ConfigurationIssue(setting, issue))
            ]
        | FrameResult.LocalFailure failure ->
            let code = Frames.failureCode failure

            [
                MessageCatalogs.renderCli
                    language
                    (CliMessage.Failure(ClientFailure.ofFrame failure))
                line language (CliMessages.key CliMessage.CodeLabel) code
            ]

    let bytesForWithDetails details language request (frame: FrameReply) =
        let lines = resultLinesWithDetails details language request frame.Result
        let warning = label language (CliMessages.key CliMessage.UncertainDelivery)

        let lines =
            if frame.Delivery = DeliveryState.Uncertain && not (List.contains warning lines) then
                lines @ [ warning ]
            else
                lines

        lines |> String.concat "\n" |> fun text -> Encoding.UTF8.GetBytes(text + "\n")

    let resultLines language request value =
        resultLinesWithDetails false language request value

    let bytesFor language request frame =
        bytesForWithDetails false language request frame

    let bytes language frame = bytesFor language None frame
