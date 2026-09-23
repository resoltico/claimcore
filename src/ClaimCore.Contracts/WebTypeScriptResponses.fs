namespace ClaimCore.Contracts

open System.Text

[<RequireQualifiedAccess>]
module internal WebTypeScriptResponses =
    let private moduleBytes imports body =
        [ "/* Generated from ClaimCore.Contracts. Do not edit. */" ]
        @ imports
        @ [ "" ]
        @ body
        @ [ "" ]
        |> String.concat "\n"
        |> Encoding.UTF8.GetBytes

    let private responseMap name endpoints =
        let properties =
            endpoints
            |> List.map (fun endpoint ->
                "  readonly "
                + System.Text.Json.JsonSerializer.Serialize(endpoint.Identifier)
                + ": "
                + Schema.typeScript endpoint.Response
                + ";")

        [ "export type " + name + " = {" ] @ properties @ [ "};" ]

    let private endpointGroup identifiers (projection: ContractModel) =
        projection.WebEndpoints
        |> List.filter (fun endpoint -> identifiers |> Set.contains endpoint.Identifier)

    let private readModule projection =
        let endpoints =
            endpointGroup
                (Set.ofList
                    [
                        "session"
                        "session.login"
                        "session.logout"
                        "definition"
                        "case.get"
                        "case.list"
                        "case.history"
                        "operation.observe"
                    ])
                projection

        moduleBytes
            [
                "import type { Rejection } from \"./web-v2.types.diagnostics\";"
                "import type { CaseSummary, CurrentCase, DefinitionPayload, Fault, HistoryEntry, Receipt, SessionSnapshot } from \"./web-v2.types.core\";"
            ]
            (responseMap "WebV2ReadResponseByEndpoint" endpoints)

    let private commandModule projection =
        let endpoints =
            endpointGroup (Set.ofList [ "command.prepare"; "command.execute" ]) projection

        moduleBytes
            [
                "import type { Rejection } from \"./web-v2.types.diagnostics\";"
                "import type { Fault, Receipt } from \"./web-v2.types.core\";"
                "import type { AdvisoryReview, DefiniteExecution, PreparationDetails, PreparationSummary, RecoveryRejection } from \"./web-v2.types.recovery\";"
            ]
            (responseMap "WebV2CommandResponseByEndpoint" endpoints)

    let private recoveryModule projection =
        let endpoints =
            projection.WebEndpoints
            |> List.filter (fun endpoint -> endpoint.Identifier.StartsWith("recovery."))

        moduleBytes
            [
                "import type { Fault, Receipt } from \"./web-v2.types.core\";"
                "import type { DefiniteExecution, PreparationDetails, PreparationSummary, RecoveryImportPreview, RecoveryInspection, RecoveryPage, RecoveryRejection, RevokedOperation } from \"./web-v2.types.recovery\";"
            ]
            (responseMap "WebV2RecoveryResponseByEndpoint" endpoints)

    let private indexModule =
        moduleBytes
            [
                "import type { WebV2EndpointId } from \"./web-v2.endpoint-catalog\";"
                "import type { WebV2ReadResponseByEndpoint } from \"./web-v2.types.responses.read\";"
                "import type { WebV2CommandResponseByEndpoint } from \"./web-v2.types.responses.command\";"
                "import type { WebV2RecoveryResponseByEndpoint } from \"./web-v2.types.responses.recovery\";"
            ]
            [
                "export type WebV2ResponseByEndpoint = WebV2ReadResponseByEndpoint & WebV2CommandResponseByEndpoint & WebV2RecoveryResponseByEndpoint;"
                "export type WebV2Response<K extends WebV2EndpointId> = WebV2ResponseByEndpoint[K];"
                "export type EndpointOutcome = WebV2Response<WebV2EndpointId>;"
            ]

    let artifacts projection =
        [
            "web-v2.types.responses.read.ts", readModule projection
            "web-v2.types.responses.command.ts", commandModule projection
            "web-v2.types.responses.recovery.ts", recoveryModule projection
            "web-v2.types.responses.ts", indexModule
        ]
