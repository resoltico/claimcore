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

    type private Presenter = string -> Invocation option -> ServiceReply -> string list

    let private presenter (endpoint: Endpoint<'Input, 'Output>) render : EndpointId * Presenter =
        endpoint.Id,
        (fun language request value ->
            ServiceReply.value endpoint value
            |> Option.defaultWith (fun () -> invalidOp "An admitted reply must remain valid.")
            |> render language request)

    let private read endpoint render =
        presenter endpoint (fun language request outcome ->
            DiagnosticText.readLinesFor
                language
                (DiagnosticText.reference request)
                (render language)
                outcome)

    let private readPresenter endpoint render = read endpoint render |> snd

    let private pagePresenter endpoint metadata =
        readPresenter endpoint (fun language (reply: Presented<_>) ->
            let count, cursor, basis = metadata reply.Data

            ViewText.render language reply.View
            @ TextObservation.footer language count cursor basis false)

    let private observedPresenter endpoint basis =
        readPresenter endpoint (fun language (reply: Presented<_>) ->
            ViewText.render language reply.View
            @ [ TextObservation.line language (basis reply.Data) ])

    // This exhaustive endpoint table guarantees every human reply selects a typed renderer.
    let private presenterFor =
        function
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseTimeline) ->
            pagePresenter
                Api.caseTimeline
                (fun (page: ClaimCore.Application.Contracts.CaseTimelinePage) ->
                    page.Items.Length, page.NextCursor, page.Basis)
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.ReminderGet) ->
            observedPresenter
                Api.reminder
                (fun (reply: ClaimCore.Application.Contracts.ReminderRead) -> reply.Basis)
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.ReminderList) ->
            pagePresenter Api.reminders (fun (page: ClaimCore.Application.Contracts.ReminderPage) ->
                page.Items.Length, page.NextCursor, page.Basis)
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.WorkViews) ->
            observedPresenter
                Api.workViews
                (fun (reply: ClaimCore.Application.Contracts.WorkViews) -> reply.Basis)
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseGet) ->
            observedPresenter
                Api.caseGet
                (fun (reply: ClaimCore.Application.Contracts.CaseReadModel) -> reply.Work.Basis)
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseAssignees) ->
            pagePresenter
                Api.caseAssignees
                (fun (page: ClaimCore.Application.Contracts.AssigneePage) ->
                    page.Items.Length, page.NextCursor, page.Basis)
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseList) ->
            pagePresenter
                Api.caseList
                (fun (page: ClaimCore.Application.Contracts.CaseSummaryPage) ->
                    page.Items.Length, page.NextCursor, page.Basis)
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.CaseSummary) ->
            observedPresenter
                Api.caseSummary
                (fun (reply: ClaimCore.Application.Contracts.CaseWorklistSummary) -> reply.Basis)
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.Context) ->
            readPresenter Api.context (fun language reply -> ViewText.render language reply.View)
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.AuthorityList) ->
            pagePresenter
                Api.authorityList
                (fun (page: ClaimCore.Application.Contracts.ActorReviewPage) ->
                    page.Items.Length, page.NextCursor, page.Basis)
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.AuthorityGrants) ->
            pagePresenter
                Api.authorityGrants
                (fun (page: ClaimCore.Application.Contracts.AuthorityGrantPage) ->
                    page.Items.Length, page.NextCursor, page.Basis)
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.AuthorityHistory) ->
            pagePresenter
                Api.authorityHistory
                (fun (page: ClaimCore.Application.Contracts.AuthorityHistoryPage) ->
                    page.Entries.Length, page.NextCursor, page.Basis)
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.AuthorityRequests) ->
            pagePresenter
                Api.authorityRequests
                (fun (page: ClaimCore.Application.Contracts.AccessRequestPage) ->
                    page.Items.Length, page.NextCursor, page.Basis)
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.AuthorityRequestsDismiss) ->
            readPresenter Api.authorityRequestsDismiss (fun language reply ->
                ViewText.render language reply.View)
        | EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Preview) ->
            presenter Api.operationPreview OperationText.previewLines |> snd
        | EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Execute) ->
            presenter Api.operationExecute (fun language request reply ->
                OperationText.executeLinesFor language request reply)
            |> snd
        | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.OperationGet) ->
            presenter Api.operationGet (fun language _ reply ->
                OperationText.getLines language reply.Outcome)
            |> snd
        | EndpointId.Host HostEndpoint.Session ->
            readPresenter Api.session (fun language reply -> ViewText.render language reply.View)
        | EndpointId.Host HostEndpoint.Logout ->
            readPresenter Api.logout (fun language reply -> ViewText.render language reply.View)

    let private replyLines language request (reply: ServiceReply) =
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
        | _ -> presenterFor reply.Route.Id language request reply

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
