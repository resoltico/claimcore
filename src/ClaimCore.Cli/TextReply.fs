namespace ClaimCore.Cli

open System
open System.Globalization
open System.Text
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Json

module internal TextReply =
    open TextReplyFormat

    type private Presenter = string -> Invocation option -> ServiceReply -> string list

    let private presenter (endpoint: Endpoint<'Input, 'Output>) render : EndpointId * Presenter =
        endpoint.Id,
        (fun language request value ->
            ServiceReply.value endpoint value
            |> Option.defaultWith (fun () -> invalidOp "An admitted reply must remain valid.")
            |> render language request)

    let private read endpoint render =
        presenter endpoint (fun language _ outcome ->
            TextDiagnosticReply.readLines language (render language) outcome)

    let private readPresenter endpoint render = read endpoint render |> snd

    // This exhaustive endpoint table guarantees every human reply selects a typed renderer.
    // fsharplint:disable-next-line CyclomaticComplexity
    let private presenterFor =
        function
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseTimeline) ->
            readPresenter Api.caseTimeline TextTimelineReply.page
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.ReminderGet) ->
            readPresenter Api.reminder TextWorkReply.single
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.ReminderList) ->
            readPresenter Api.reminders TextWorkReply.page
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.WorkOverview) ->
            readPresenter Api.workOverview TextWorkReply.overview
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseViews) ->
            readPresenter Api.caseViews TextWorkReply.caseViews
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.ReminderViews) ->
            readPresenter Api.reminderViews TextWorkReply.reminderViews
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseActivity) ->
            readPresenter Api.caseActivity TextWorkReply.activity
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseGet) ->
            readPresenter Api.caseGet TextCaseReply.caseLines
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseAssignees) ->
            readPresenter Api.caseAssignees TextCaseReply.assigneePage
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseList) ->
            readPresenter Api.caseList TextCaseReply.casePage
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseHistory) ->
            readPresenter Api.caseHistory TextCaseReply.historyLines
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseSummary) ->
            readPresenter Api.caseSummary TextContextReply.summary
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.Context) ->
            readPresenter Api.context TextContextReply.context
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.Observe) ->
            presenter Api.operationObserve (fun language _ reply ->
                TextDiagnosticReply.readLines
                    language
                    (TextOperationReply.receiptLines language "ACCEPTED")
                    reply.Outcome)
            |> snd
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.AuthorityList) ->
            readPresenter Api.authorityList TextAuthorityReply.actorPage
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.AuthorityGrants) ->
            readPresenter Api.authorityGrants TextAuthorityReply.grantPage
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.AuthorityHistory) ->
            readPresenter Api.authorityHistory TextAuthorityReply.historyPage
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.AuthorityRequests) ->
            readPresenter Api.authorityRequests TextAuthorityReply.requests
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.AuthorityRequestsDismiss) ->
            readPresenter Api.authorityRequestsDismiss (fun language () ->
                [ label language "label.requestDismissed" ])
        | EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Preview) ->
            presenter Api.operationPreview TextOperationReply.previewLines |> snd
        | EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Execute) ->
            presenter Api.operationExecute (fun language _ reply ->
                TextOperationReply.executeLines language reply)
            |> snd
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.Status) ->
            presenter Api.operationStatus (fun language _ reply ->
                TextOperationReply.statusLines language reply.Outcome)
            |> snd
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.Resolve) ->
            presenter Api.operationResolve (fun language _ reply ->
                TextOperationReply.resolveLines language reply.Outcome)
            |> snd
        | EndpointId.Host(HostEndpoint.Session | HostEndpoint.Logout) ->
            fun _ _ _ -> invalidOp "Host authentication replies have no human command surface."

    let private replyLines language request (reply: ServiceReply) =
        presenterFor reply.Route.Id language request reply

    let resultLines language request =
        function
        | FrameResult.Reply reply -> replyLines language request reply
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

    let bytesFor language request (frame: FrameReply) =
        let lines = resultLines language request frame.Result
        let warning = label language (CliMessages.key CliMessage.UncertainDelivery)

        let lines =
            if frame.Delivery = DeliveryState.Uncertain && not (List.contains warning lines) then
                lines @ [ warning ]
            else
                lines

        lines |> String.concat "\n" |> fun text -> Encoding.UTF8.GetBytes(text + "\n")

    let bytes language frame = bytesFor language None frame
