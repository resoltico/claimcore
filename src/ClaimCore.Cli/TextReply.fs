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

    let private presenters =
        [
            read Api.caseGet TextCaseReply.caseLines
            read Api.caseList TextCaseReply.casePage
            read Api.caseHistory TextCaseReply.historyLines
            presenter Api.operationObserve (fun language _ reply ->
                TextDiagnosticReply.readLines
                    language
                    (TextOperationReply.receiptLines language "ACCEPTED")
                    reply.Outcome)
            read Api.authorityList TextAuthorityReply.actorPage
            read Api.authorityGrants TextAuthorityReply.grantPage
            read Api.authorityHistory TextAuthorityReply.historyPage
            presenter Api.operationPreview TextOperationReply.previewLines
            presenter Api.operationExecute (fun language _ reply ->
                TextOperationReply.executeLines language reply)
            presenter Api.operationResolve (fun language _ reply ->
                TextOperationReply.resolveLines language reply.Outcome)
        ]
        |> Map.ofList

    let private replyLines language request (reply: ServiceReply) =
        match Map.tryFind reply.Route.Id presenters with
        | Some render -> render language request reply
        | None -> TextGenericReply.render language reply

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
