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

    let private actorScreenPresenter endpoint =
        presenter endpoint (fun details language _ reply ->
            match reply with
            | RepresentationReply.Human value -> HumanText.read details language value
            | RepresentationReply.Data _ -> invalidOp "Human text requires a HUMAN response.")
        |> snd

    let private presenterFor =
        function
        | EndpointId.Actor(ActorEndpoint.Query QueryTaskId.CaseNotes) ->
            actorScreenPresenter Api.caseNotes
        | EndpointId.Actor(ActorEndpoint.Query QueryTaskId.CaseTimeline) ->
            actorScreenPresenter Api.caseTimeline
        | EndpointId.Actor(ActorEndpoint.Query QueryTaskId.ReminderGet) ->
            actorScreenPresenter Api.reminder
        | EndpointId.Actor(ActorEndpoint.Query QueryTaskId.ReminderList) ->
            actorScreenPresenter Api.reminders
        | EndpointId.Actor(ActorEndpoint.Query QueryTaskId.WorkViews) ->
            actorScreenPresenter Api.workViews
        | EndpointId.Actor(ActorEndpoint.Query QueryTaskId.CaseGet) ->
            presenter Api.caseGet (fun details language _ reply ->
                match reply with
                | RepresentationReply.Human value -> HumanText.read details language value
                | RepresentationReply.Data _ -> invalidOp "Human text requires a HUMAN response.")
            |> snd
        | EndpointId.Actor(ActorEndpoint.Query QueryTaskId.CaseAssignees) ->
            actorScreenPresenter Api.caseAssignees
        | EndpointId.Actor(ActorEndpoint.Query QueryTaskId.CaseList) ->
            actorScreenPresenter Api.caseList
        | EndpointId.Actor(ActorEndpoint.Query QueryTaskId.CaseSummary) ->
            actorScreenPresenter Api.caseSummary
        | EndpointId.Actor(ActorEndpoint.Query QueryTaskId.Context) ->
            actorScreenPresenter Api.context
        | EndpointId.Actor(ActorEndpoint.Query QueryTaskId.AuthorityList) ->
            actorScreenPresenter Api.authorityList
        | EndpointId.Actor(ActorEndpoint.Query QueryTaskId.AuthorityGrants) ->
            actorScreenPresenter Api.authorityGrants
        | EndpointId.Actor(ActorEndpoint.Query QueryTaskId.AuthorityHistory) ->
            actorScreenPresenter Api.authorityHistory
        | EndpointId.Actor(ActorEndpoint.Query QueryTaskId.AuthorityRequests) ->
            actorScreenPresenter Api.authorityRequests
        | EndpointId.Actor(ActorEndpoint.Query QueryTaskId.AuthorityRequestsDismiss) ->
            actorScreenPresenter Api.authorityRequestsDismiss
        | EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Preview) ->
            presenter Api.operationPreview OperationText.previewLinesWithDetails |> snd
        | EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Execute) ->
            presenter Api.operationExecute OperationText.executeLinesForWithDetails |> snd
        | EndpointId.Actor(ActorEndpoint.Query QueryTaskId.OperationGet) ->
            presenter Api.operationGet (fun details language _ reply ->
                OperationText.getLinesWithDetails details language reply)
            |> snd
        | EndpointId.Host HostEndpoint.Session -> screenPresenter Api.session
        | EndpointId.Host HostEndpoint.Logout -> screenPresenter Api.logout

    let private replyLines details language request (reply: ServiceReply) =
        presenterFor reply.Route.Id details language request reply

    let resultLinesWithDetails details language request =
        function
        | FrameResult.Reply reply -> replyLines details language request reply
        | FrameResult.HostFailure failure ->
            [
                label
                    language
                    (DiagnosticCode.key (Vocabulary.token HostFailureCode.vocabulary failure.Code))
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
