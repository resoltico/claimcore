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
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseNotes) ->
            screenPresenter Api.caseNotes
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseTimeline) ->
            screenPresenter Api.caseTimeline
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.ReminderGet) ->
            screenPresenter Api.reminder
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.ReminderList) ->
            screenPresenter Api.reminders
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.WorkViews) ->
            screenPresenter Api.workViews
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseGet) -> screenPresenter Api.caseGet
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseAssignees) ->
            screenPresenter Api.caseAssignees
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseList) ->
            screenPresenter Api.caseList
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseSummary) ->
            screenPresenter Api.caseSummary
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.Context) -> screenPresenter Api.context
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.AuthorityList) ->
            screenPresenter Api.authorityList
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.AuthorityGrants) ->
            screenPresenter Api.authorityGrants
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.AuthorityHistory) ->
            screenPresenter Api.authorityHistory
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.AuthorityRequests) ->
            screenPresenter Api.authorityRequests
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.AuthorityRequestsDismiss) ->
            screenPresenter Api.authorityRequestsDismiss
        | EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Preview) ->
            presenter Api.operationPreview OperationText.previewLinesWithDetails |> snd
        | EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Execute) ->
            presenter Api.operationExecute OperationText.executeLinesForWithDetails |> snd
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.OperationGet) ->
            presenter Api.operationGet (fun details language _ reply ->
                OperationText.getLinesWithDetails details language reply.Outcome)
            |> snd
        | EndpointId.Host HostEndpoint.Session -> screenPresenter Api.session
        | EndpointId.Host HostEndpoint.Logout -> screenPresenter Api.logout

    let private replyLines details language request (reply: ServiceReply) =
        let refusal =
            match reply.Route.Id with
            | EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Preview) ->
                ServiceReply.value Api.operationPreview reply
                |> Option.bind (fun value ->
                    match value.Outcome with
                    | Outcome.Rejected refusal -> Some refusal
                    | _ -> None)
            | EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Execute) ->
                ServiceReply.value Api.operationExecute reply
                |> Option.bind (fun value ->
                    match value.Outcome with
                    | PresentedExecuteResult.Rejected refusal -> Some refusal
                    | _ -> None)
            | _ -> None

        match refusal with
        | Some(Refusal.RevisionConflict _ as refusal) ->
            DiagnosticText.refusalLinesWithView
                (ServiceReply.conflictView reply)
                language
                (DiagnosticText.reference request)
                refusal
        | _ -> presenterFor reply.Route.Id details language request reply

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
