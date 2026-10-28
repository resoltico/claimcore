namespace ClaimCore.Cli

open System
open System.Globalization
open System.Text
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Json

module internal TextDiagnosticReply =
    open TextReplyFormat

    let diagnosticTargetIdentity target =
        ContractDiscovery.inputLabel (InteractionInput.Target target)

    let refusalLines language refusal =
        let render code parameters =
            let arguments =
                parameters
                |> List.map (fun (name, value) -> name, number (int64 value))
                |> Map.ofList

            MessageCatalogs.render language (DiagnosticCode.key code) arguments

        let messages =
            match refusal with
            | Refusal.InvalidInput issues ->
                InputIssues.toList issues
                |> List.map (fun issue ->
                    let code, parameters = DiagnosticCodecs.describeIssue issue

                    label language (diagnosticTargetIdentity issue.Target)
                    + ": "
                    + render code parameters)
            | refusal ->
                let code, parameters = DiagnosticCodecs.describe refusal
                let message = render code parameters

                match refusal with
                | Refusal.InvalidCursor ->
                    [
                        label language (diagnosticTargetIdentity InputTarget.Cursor)
                        + ": "
                        + message
                    ]
                | Refusal.OperationResolved ->
                    [
                        label language (diagnosticTargetIdentity InputTarget.OperationId)
                        + ": "
                        + message
                    ]
                | _ -> [ message ]

        messages
        @ [
            line language (CliMessages.key CliMessage.CodeLabel) (DiagnosticCodecs.code refusal)
            line
                language
                (CliMessages.key CliMessage.ActionLabel)
                (RecommendedAction.token refusal.Action)
        ]

    let faultLines language fault =
        let code = CoreFault.token fault

        [
            label language (DiagnosticCode.key code)
            line language (CliMessages.key CliMessage.CodeLabel) code
            line
                language
                (CliMessages.key CliMessage.ActionLabel)
                (RecommendedAction.token fault.Action)
        ]

    let readLines language success outcome =
        Outcome.fold success (refusalLines language) (faultLines language) outcome
