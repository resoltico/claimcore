namespace ClaimCore.Contracts

open ClaimCore.Application

module internal WebEndpointCatalog =
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

    let private cases responses (semantic: SemanticCoreContract) =
        [
            endpoint
                responses
                "case.get"
                "POST"
                "/api/v2/cases/get"
                (json (EndpointInputs.caseReference semantic))
                None
            endpoint
                responses
                "case.list"
                "POST"
                "/api/v2/cases/list"
                (json (EndpointInputs.cursor semantic.MaximumPageSize))
                None
            endpoint
                responses
                "case.history"
                "POST"
                "/api/v2/cases/history"
                (json (EndpointInputs.history semantic))
                None
            endpoint
                responses
                "operation.observe"
                "POST"
                "/api/v2/operations/observe"
                (json EndpointInputs.operation)
                None
            endpoint
                responses
                "command.prepare"
                "POST"
                "/api/v2/operations/prepare"
                (json (ProjectionSchema.commandDraft semantic))
                None
            endpoint
                responses
                "command.execute"
                "POST"
                "/api/v2/operations/submit"
                (json EndpointInputs.recoveryResolution)
                None
        ]

    let private recoveryJson responses (semantic: SemanticCoreContract) =
        [
            endpoint
                responses
                "recovery.list"
                "POST"
                "/api/v2/recovery/list"
                (json (EndpointInputs.cursor semantic.MaximumPageSize))
                None
            endpoint
                responses
                "recovery.inspect"
                "POST"
                "/api/v2/recovery/inspect"
                (json EndpointInputs.operation)
                None
            endpoint
                responses
                "recovery.resolve"
                "POST"
                "/api/v2/recovery/resolve"
                (json EndpointInputs.recoveryResolution)
                None
            endpoint
                responses
                "recovery.dismiss"
                "POST"
                "/api/v2/recovery/dismiss"
                (json EndpointInputs.recoveryDismiss)
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

    let all (semantic: SemanticCoreContract) =
        let responses = WebResponseSchemas.all () |> Map.ofList

        sessions responses
        @ cases responses semantic
        @ recoveryJson responses semantic
        @ recoveryRaw responses
