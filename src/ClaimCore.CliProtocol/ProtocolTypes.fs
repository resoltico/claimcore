namespace ClaimCore.Cli

open System
open System.Text.Json

[<RequireQualifiedAccess>]
type Endpoint =
    | CommandPrepare
    | CommandExecute
    | CaseGet
    | CaseList
    | CaseHistory
    | OperationObserve
    | RecoveryList
    | RecoveryInspect
    | RecoveryResolve
    | RecoveryDismiss
    | RecoveryExport
    | RecoveryImportEnvelopePreview
    | RecoveryImportEnvelopeRetain
    | RecoveryImportRecordPreview
    | RecoveryImportRecordRetain

module Endpoint =
    let private identifiers =
        Map.ofList
            [
                Endpoint.CommandPrepare, "command.prepare"
                Endpoint.CommandExecute, "command.execute"
                Endpoint.CaseGet, "case.get"
                Endpoint.CaseList, "case.list"
                Endpoint.CaseHistory, "case.history"
                Endpoint.OperationObserve, "operation.observe"
                Endpoint.RecoveryList, "recovery.list"
                Endpoint.RecoveryInspect, "recovery.inspect"
                Endpoint.RecoveryResolve, "recovery.resolve"
                Endpoint.RecoveryDismiss, "recovery.dismiss"
                Endpoint.RecoveryExport, "recovery.export"
                Endpoint.RecoveryImportEnvelopePreview, "recovery.importEnvelopePreview"
                Endpoint.RecoveryImportEnvelopeRetain, "recovery.importEnvelopeRetain"
                Endpoint.RecoveryImportRecordPreview, "recovery.importRecordPreview"
                Endpoint.RecoveryImportRecordRetain, "recovery.importRecordRetain"
            ]

    let identifier endpoint = Map.find endpoint identifiers

    let all =
        [
            Endpoint.CommandPrepare
            Endpoint.CommandExecute
            Endpoint.CaseGet
            Endpoint.CaseList
            Endpoint.CaseHistory
            Endpoint.OperationObserve
            Endpoint.RecoveryList
            Endpoint.RecoveryInspect
            Endpoint.RecoveryResolve
            Endpoint.RecoveryDismiss
            Endpoint.RecoveryExport
            Endpoint.RecoveryImportEnvelopePreview
            Endpoint.RecoveryImportEnvelopeRetain
            Endpoint.RecoveryImportRecordPreview
            Endpoint.RecoveryImportRecordRetain
        ]

    let tryParse value =
        all |> List.tryFind (fun endpoint -> identifier endpoint = value)

[<NoEquality; NoComparison>]
type ProtocolFailure =
    {
        Code: string
        Message: string
        Path: string
    }

module ProtocolFailure =
    let create code message path =
        {
            Code = code
            Message = message
            Path = path
        }

[<NoEquality; NoComparison>]
type Invocation =
    {
        Endpoint: Endpoint
        Input: JsonElement
        TimeoutMilliseconds: int option
    }

[<NoEquality; NoComparison; RequireQualifiedAccess>]
type InvocationResult =
    | Result of endpoint: Endpoint * outcome: JsonElement
    | ProtocolFailure of ProtocolFailure

module InvocationResult =
    let endpoint result =
        match result with
        | InvocationResult.Result(endpoint, _) -> Some endpoint
        | InvocationResult.ProtocolFailure _ -> None
