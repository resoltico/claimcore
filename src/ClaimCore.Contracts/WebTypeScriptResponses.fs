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

    let private readIdentifiers =
        Set.ofList
            [
                "session"
                "session.logout"
                "definition"
                "case.get"
                "case.list"
                "case.history"
                "operation.observe"
            ]

    let private authorityIdentifiers =
        Set.ofList
            [
                "authority.register"
                "authority.setGrant"
                "authority.setEnabled"
                "authority.observe"
                "authority.approveCopySigner"
                "authority.approveCopyDeletion"
                "authority.approveCopyAdoption"
                "authority.approveWriterHandoff"
                "authority.reviewRealDataActivation"
                "authority.approveRealDataActivation"
            ]

    let private lifecycleIdentifiers =
        Set.ofList
            [
                "lifecycle.review"
                "lifecycle.apply"
                "lifecycle.approve"
                "tombstone.review"
                "tombstone.approvePrune"
                "tombstone.approveTerminal"
                "tombstone.changeHold"
            ]

    let private commandIdentifiers = Set.ofList [ "command.prepare"; "command.execute" ]

    let private recoveryIdentifiers (projection: ContractModel) =
        projection.WebEndpoints
        |> List.map _.Identifier
        |> List.filter (fun identifier -> identifier.StartsWith("recovery."))
        |> Set.ofList

    let private readModule projection =
        let endpoints = endpointGroup readIdentifiers projection

        moduleBytes
            [
                "import type { Rejection } from \"./web-v3.types.diagnostics\";"
                "import type { CaseSummary, CurrentCase, DefinitionPayload, Fault, HistoryEntry, Receipt, SessionSnapshot } from \"./web-v3.types.core\";"
            ]
            (responseMap "WebV3ReadResponseByEndpoint" endpoints)

    let private commandModule projection =
        let endpoints = endpointGroup commandIdentifiers projection

        moduleBytes
            [
                "import type { Rejection } from \"./web-v3.types.diagnostics\";"
                "import type { Fault, Receipt } from \"./web-v3.types.core\";"
                "import type { AdvisoryReview, DefiniteExecution, PreparationDetails, PreparationSummary } from \"./web-v3.types.recovery\";"
            ]
            (responseMap "WebV3CommandResponseByEndpoint" endpoints)

    let private authorityModule projection =
        let endpoints = endpointGroup authorityIdentifiers projection

        moduleBytes [] (responseMap "WebV3AuthorityResponseByEndpoint" endpoints)

    let private lifecycleModule projection =
        let endpoints = endpointGroup lifecycleIdentifiers projection

        moduleBytes
            [ "import type { Fault } from \"./web-v3.types.core\";" ]
            (responseMap "WebV3LifecycleResponseByEndpoint" endpoints)

    let private recoveryModule projection =
        let endpoints = endpointGroup (recoveryIdentifiers projection) projection

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

        let groups =
            [
                readIdentifiers
                authorityIdentifiers
                lifecycleIdentifiers
                commandIdentifiers
                recoveryIdentifiers projection
            ]

        let covered = groups |> List.fold Set.union Set.empty
        let counted = groups |> List.sumBy Set.count

        if covered <> expected || counted <> expected.Count then
            invalidOp "Every Web endpoint must belong to exactly one generated response group."

        [
            "web-v3.types.responses.read.ts", readModule projection
            "web-v3.types.responses.authority.ts", authorityModule projection
            "web-v3.types.responses.lifecycle.ts", lifecycleModule projection
            "web-v3.types.responses.command.ts", commandModule projection
            "web-v3.types.responses.recovery.ts", recoveryModule projection
            "web-v3.types.responses.ts", indexModule
        ]
