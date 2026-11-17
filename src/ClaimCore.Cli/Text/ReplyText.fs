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

    type private Presenter =
        bool -> RenderingContext -> Invocation option -> ServiceReply -> string list

    let private presenter (endpoint: Endpoint<'Input, 'Output>) render : EndpointId * Presenter =
        endpoint.Id,
        (fun details rendering request value ->
            ServiceReply.value endpoint value
            |> Option.defaultWith (fun () -> invalidOp "An admitted reply must remain valid.")
            |> render details rendering request)

    let private read endpoint render =
        presenter endpoint (fun details rendering request outcome ->
            DiagnosticText.readLinesFor
                rendering
                (DiagnosticText.reference request)
                (render details rendering)
                outcome)

    let private readPresenter endpoint render = read endpoint render |> snd

    let private screenPresenter endpoint =
        readPresenter endpoint (fun details rendering (reply: Presented<_>) ->
            ViewText.renderSelected details rendering reply.View)

    let private presenterFor =
        function
        | EndpointId.Actor(ActorEndpoint.Query QueryTaskId.OperationGet) ->
            presenter Api.operationGet (fun details rendering _ reply ->
                OperationText.getLinesWithDetails details rendering reply)
            |> snd
        | EndpointId.Actor(ActorEndpoint.Command(CommandTaskId.TerminateOwnSessions | CommandTaskId.TerminatePersonSessions)) ->
            fun details rendering _request (reply: ServiceReply) ->
                let render =
                    function
                    | RepresentationReply.Human(HumanSessionTerminationResult.Succeeded(_, document))
                    | RepresentationReply.Human(HumanSessionTerminationResult.OutcomeUnknown document) ->
                        ViewText.renderDocument details rendering document
                    | RepresentationReply.Human(HumanSessionTerminationResult.Rejected rejection) ->
                        HumanText.rejection details rendering rejection
                    | RepresentationReply.Human(HumanSessionTerminationResult.Failed fault) ->
                        HumanText.fault rendering fault
                    | RepresentationReply.Data _ ->
                        invalidOp "Human text requires a HUMAN response."

                match reply.Route.Id with
                | EndpointId.Actor(ActorEndpoint.Command CommandTaskId.TerminateOwnSessions) ->
                    ServiceReply.value Api.terminateOwnSessions reply
                    |> Option.map render
                    |> Option.get
                | EndpointId.Actor(ActorEndpoint.Command CommandTaskId.TerminatePersonSessions) ->
                    ServiceReply.value Api.terminatePersonSessions reply
                    |> Option.map render
                    |> Option.get
                | _ -> invalidOp "A session command is required."
        | EndpointId.Actor(ActorEndpoint.Query _ | ActorEndpoint.Command _) ->
            fun details rendering request (reply: ServiceReply) ->
                match EndpointValues.readReply reply.Typed with
                | Some(RepresentationReply.Human value) -> HumanText.read details rendering value
                | Some(RepresentationReply.Data outcome) ->
                    DiagnosticText.readLinesFor
                        rendering
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

    let private replyLines details (rendering: RenderingContext) request (reply: ServiceReply) =
        presenterFor reply.Route.Id details rendering request reply

    let resultLinesWithDetails details (rendering: RenderingContext) request =
        function
        | FrameResult.Reply reply -> replyLines details rendering request reply
        | FrameResult.HostFailure failure ->
            [
                // The shared host messages advise browser actions; the CLI states its own next step.
                match failure.Code with
                | HostFailureCode.ContractMismatch ->
                    MessageCatalogs.renderCli
                        rendering.Language
                        (CliMessage.Failure ClientFailure.ContractMismatch)
                | HostFailureCode.InstallationMismatch ->
                    MessageCatalogs.renderCli
                        rendering.Language
                        (CliMessage.Failure ClientFailure.InstallationMismatch)
                | code ->
                    label
                        rendering
                        (DiagnosticCode.key (Vocabulary.token HostFailureCode.vocabulary code))
                line
                    rendering
                    (CliMessages.key CliMessage.CodeLabel)
                    (Vocabulary.token HostFailureCode.vocabulary failure.Code)
            ]
        | FrameResult.LocalFailure(CliFailure.ConfigurationIssue(setting, issue)) ->
            let setting = InputDiagnostics.settingName setting

            let issue = ConfigurationIssue.token issue

            [
                MessageCatalogs.renderCli
                    rendering.Language
                    (CliMessage.ConfigurationIssue(setting, issue))
            ]
        | FrameResult.LocalFailure failure ->
            let code = Frames.failureCode failure

            [
                MessageCatalogs.renderCli
                    rendering.Language
                    (CliMessage.Failure(ClientFailure.ofFrame failure))
                line rendering (CliMessages.key CliMessage.CodeLabel) code
            ]

    let bytesForWithDetails details (rendering: RenderingContext) request (frame: FrameReply) =
        let lines = resultLinesWithDetails details rendering request frame.Result

        let sessionCommand =
            match frame.Endpoint with
            | Some(EndpointId.Actor(ActorEndpoint.Command(CommandTaskId.TerminateOwnSessions | CommandTaskId.TerminatePersonSessions))) ->
                true
            | _ -> false

        let warnings =
            if sessionCommand then
                ViewText.renderDocument details rendering (HumanReadDocuments.terminationUnknown ())
            else
                [
                    label
                        rendering
                        (CliMessages.key (
                            if Frames.isLedgerExecution frame then
                                CliMessage.UncertainDelivery
                            else
                                CliMessage.OperationalUnresolved
                        ))
                ]

        let lines =
            if frame.Delivery = DeliveryState.Uncertain && Frames.mayChange frame then
                lines
                @ (warnings |> List.filter (fun warning -> not (List.contains warning lines)))
            else
                lines

        lines |> String.concat "\n" |> fun text -> Encoding.UTF8.GetBytes(text + "\n")

    let resultLines (rendering: RenderingContext) request value =
        resultLinesWithDetails false rendering request value

    let bytesFor (rendering: RenderingContext) request frame =
        bytesForWithDetails false rendering request frame

    let bytes (rendering: RenderingContext) frame = bytesFor rendering None frame
