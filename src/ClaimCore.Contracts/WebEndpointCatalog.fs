namespace ClaimCore.Contracts

open ClaimCore.Application

module internal WebEndpointCatalog =
    let private input (endpoints: CliEndpoint list) identifier =
        endpoints
        |> List.find (fun endpoint -> endpoint.Identifier = identifier)
        |> fun endpoint -> endpoint.Input

    let private loginInput =
        Schema.objectOf
            false
            [
                Schema.property "credential" (Schema.string None None (Some 1) None) true
                Schema.property "antiforgeryToken" (Schema.string None None (Some 1) None) true
            ]

    let private sourceDigestHeader =
        [ "X-ClaimCore-Source-Sha256", EndpointInputs.digest ]

    let private endpoint responses identifier method path body successMediaType =
        {
            Identifier = identifier
            Method = method
            Path = path
            Body = body
            Response = Map.find identifier responses
            SuccessMediaType = successMediaType
        }

    let private json input = Some(JsonBody input)

    let private raw mediaType maximumBytes headers =
        Some(RawBody(mediaType, maximumBytes, headers))

    let private sessions responses =
        [
            endpoint responses "session" "GET" "/api/v2/session" None None
            endpoint responses "session.login" "POST" "/api/v2/session/login" (json loginInput) None
            endpoint
                responses
                "session.logout"
                "POST"
                "/api/v2/session/logout"
                (json (Schema.objectOf false []))
                None
            endpoint responses "definition" "GET" "/api/v2/definition" None None
        ]

    let private cases responses (inputs: Map<string, Schema>) =
        let jsonInput identifier = inputs |> Map.find identifier |> json

        [
            endpoint responses "case.get" "POST" "/api/v2/cases/get" (jsonInput "case.get") None
            endpoint responses "case.list" "POST" "/api/v2/cases/list" (jsonInput "case.list") None
            endpoint
                responses
                "case.history"
                "POST"
                "/api/v2/cases/history"
                (jsonInput "case.history")
                None
            endpoint
                responses
                "operation.observe"
                "POST"
                "/api/v2/operations/observe"
                (jsonInput "operation.observe")
                None
            endpoint
                responses
                "command.prepare"
                "POST"
                "/api/v2/operations/prepare"
                (jsonInput "command.prepare")
                None
            endpoint
                responses
                "command.execute"
                "POST"
                "/api/v2/operations/submit"
                (json EndpointInputs.recoveryResolution)
                None
        ]

    let private recoveryJson responses (inputs: Map<string, Schema>) =
        let jsonInput identifier = inputs |> Map.find identifier |> json

        [
            endpoint
                responses
                "recovery.list"
                "POST"
                "/api/v2/recovery/list"
                (jsonInput "recovery.list")
                None
            endpoint
                responses
                "recovery.inspect"
                "POST"
                "/api/v2/recovery/inspect"
                (jsonInput "recovery.inspect")
                None
            endpoint
                responses
                "recovery.resolve"
                "POST"
                "/api/v2/recovery/resolve"
                (jsonInput "recovery.resolve")
                None
            endpoint
                responses
                "recovery.dismiss"
                "POST"
                "/api/v2/recovery/dismiss"
                (jsonInput "recovery.dismiss")
                None
            endpoint
                responses
                "recovery.export"
                "POST"
                "/api/v2/recovery/export"
                (json EndpointInputs.recoveryExportTarget)
                (Some "application/vnd.claimcore.recovery+json")
        ]

    let private recoveryRaw responses =
        [
            endpoint
                responses
                "recovery.importEnvelopePreview"
                "POST"
                "/api/v2/recovery/import-envelope/preview"
                (raw "application/vnd.claimcore.recovery+json" 131072 [])
                None
            endpoint
                responses
                "recovery.importEnvelopeRetain"
                "POST"
                "/api/v2/recovery/import-envelope/retain"
                (raw "application/vnd.claimcore.recovery+json" 131072 sourceDigestHeader)
                None
            endpoint
                responses
                "recovery.importRecordPreview"
                "POST"
                "/api/v2/recovery/import-record/preview"
                (raw "application/vnd.claimcore.canonical-command+json" 65536 [])
                None
            endpoint
                responses
                "recovery.importRecordRetain"
                "POST"
                "/api/v2/recovery/import-record/retain"
                (raw "application/vnd.claimcore.canonical-command+json" 65536 sourceDigestHeader)
                None
        ]

    let all cliEndpoints =
        let responses = WebResponseSchemas.all () |> Map.ofList

        let inputs =
            cliEndpoints
            |> List.map (fun (value: CliEndpoint) -> value.Identifier, value.Input)
            |> Map.ofList

        sessions responses
        @ cases responses inputs
        @ recoveryJson responses inputs
        @ recoveryRaw responses
