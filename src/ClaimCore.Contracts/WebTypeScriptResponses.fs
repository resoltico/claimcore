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

    // Response-file ownership follows established endpoint namespaces, not mutation authority.
    let private responseGroup identifier =
        match identifier with
        | "session"
        | "session.logout"
        | "definition" -> "read"
        | _ ->
            match identifier.Split('.') with
            | [| "case"; _ |]
            | [| "operation"; _ |] -> "read"
            | [| "authority"; _ |] -> "authority"
            | [| "lifecycle"; _ |]
            | [| "tombstone"; _ |] -> "lifecycle"
            | [| "command"; _ |] -> "command"
            | [| "recovery"; _ |] -> "recovery"
            | _ -> invalidOp "A Web endpoint has no reviewed response namespace."

    let private endpointGroup group (projection: ContractModel) =
        projection.WebEndpoints
        |> List.filter (fun endpoint -> responseGroup endpoint.Identifier = group)

    let private readModule projection =
        let endpoints = endpointGroup "read" projection

        moduleBytes
            [
                "import type { Rejection } from \"./web-v3.types.diagnostics\";"
                "import type { CaseSummary, CurrentCase, DefinitionPayload, Fault, HistoryEntry, Receipt, SessionSnapshot } from \"./web-v3.types.core\";"
            ]
            (responseMap "WebV3ReadResponseByEndpoint" endpoints)

    let private commandModule projection =
        let endpoints = endpointGroup "command" projection

        moduleBytes
            [
                "import type { Rejection } from \"./web-v3.types.diagnostics\";"
                "import type { Fault, Receipt } from \"./web-v3.types.core\";"
                "import type { AdvisoryReview, DefiniteExecution, PreparationDetails, PreparationSummary } from \"./web-v3.types.recovery\";"
            ]
            (responseMap "WebV3CommandResponseByEndpoint" endpoints)

    let private authorityModule projection =
        let endpoints = endpointGroup "authority" projection

        moduleBytes [] (responseMap "WebV3AuthorityResponseByEndpoint" endpoints)

    let private lifecycleModule projection =
        let endpoints = endpointGroup "lifecycle" projection

        moduleBytes
            [ "import type { Fault } from \"./web-v3.types.core\";" ]
            (responseMap "WebV3LifecycleResponseByEndpoint" endpoints)

    let private recoveryModule projection =
        let endpoints = endpointGroup "recovery" projection

        moduleBytes
            [
                "import type { Fault, Receipt } from \"./web-v3.types.core\";"
                "import type { DefiniteExecution, PreparationDetails, PreparationSummary, RecoveryImportPreview, RecoveryInspection, RecoveryPage, RecoveryRejection, RevokedOperation } from \"./web-v3.types.recovery\";"
            ]
            (responseMap "WebV3RecoveryResponseByEndpoint" endpoints)

    let private indexModule =
        moduleBytes
            [
                "import type { WebV3EndpointId } from \"./web-v3.endpoint-catalog\";"
                "import type { WebV3ReadResponseByEndpoint } from \"./web-v3.types.responses.read\";"
                "import type { WebV3AuthorityResponseByEndpoint } from \"./web-v3.types.responses.authority\";"
                "import type { WebV3LifecycleResponseByEndpoint } from \"./web-v3.types.responses.lifecycle\";"
                "import type { WebV3CommandResponseByEndpoint } from \"./web-v3.types.responses.command\";"
                "import type { WebV3RecoveryResponseByEndpoint } from \"./web-v3.types.responses.recovery\";"
            ]
            [
                "export type WebV3ResponseByEndpoint = WebV3ReadResponseByEndpoint & WebV3AuthorityResponseByEndpoint & WebV3LifecycleResponseByEndpoint & WebV3CommandResponseByEndpoint & WebV3RecoveryResponseByEndpoint;"
                "export type WebV3Response<K extends WebV3EndpointId> = WebV3ResponseByEndpoint[K];"
                "export type EndpointOutcome = WebV3Response<WebV3EndpointId>;"
            ]

    let artifacts projection =
        let expected = projection.WebEndpoints |> List.map _.Identifier |> Set.ofList

        if expected.Count <> projection.WebEndpoints.Length then
            invalidOp "Generated Web response identifiers must be unique."

        [
            "web-v3.types.responses.read.ts", readModule projection
            "web-v3.types.responses.authority.ts", authorityModule projection
            "web-v3.types.responses.lifecycle.ts", lifecycleModule projection
            "web-v3.types.responses.command.ts", commandModule projection
            "web-v3.types.responses.recovery.ts", recoveryModule projection
            "web-v3.types.responses.ts", indexModule
        ]
