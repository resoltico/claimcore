namespace ClaimCore.Contracts

module CliRemoteSchemas =
    let private property = Schema.property
    let private token = WireSchema.token
    let private version = WireSchema.number 4

    let private envelope kind endpoint fields =
        Schema.objectOf
            false
            ([
                property "protocolVersion" version true
                property "kind" (token kind) true
                property "endpoint" (token endpoint) true
             ]
             @ fields)

    let private webResponse projection identifier =
        projection.WebEndpoints
        |> List.find (fun item -> item.Identifier = identifier)
        |> _.Response

    let result projection identifier =
        envelope "result" identifier [ property "service" (webResponse projection identifier) true ]

    let serviceFailure identifier =
        envelope
            "serviceFailure"
            identifier
            [ property "service" WebSchemaDefinitions.hostFailure true ]

    let localFailure identifier =
        let definite =
            envelope
                "localFailure"
                identifier
                [
                    property
                        "code"
                        (WireSchema.enumeration
                            [
                                "CLI_CONFIGURATION_INVALID"
                                "CLI_AUTHENTICATION_UNAVAILABLE"
                                "CLI_SERVICE_UNAVAILABLE"
                                "CLI_SERVICE_REPLY_INVALID"
                                "CLI_PRIVATE_SOURCE_INVALID"
                            ])
                        true
                    property "executionPhase" (token "NOT_STARTED") true
                    property "action" (token "STOP_AND_INVESTIGATE") true
                ]

        let uncertain =
            envelope
                "localFailure"
                identifier
                [
                    property "code" (token "CLI_DELIVERY_UNCONFIRMED") true
                    property "executionPhase" (token "STARTED_UNCONFIRMED") true
                    property "action" (token "RECOVER_EXACT") true
                ]

        let observed =
            envelope
                "localFailure"
                identifier
                [
                    property "code" (token "CLI_PRIVATE_DESTINATION_INVALID") true
                    property "executionPhase" (token "RESULT_OBSERVED") true
                    property "action" (token "STOP_AND_INVESTIGATE") true
                ]

        Schema.oneOf [ definite; uncertain; observed ]

    let exported =
        envelope
            "exported"
            "recovery.export"
            [
                property "operationId" ScalarSchemas.uuid true
                property "mediaType" (token "application/vnd.claimcore.recovery+json") true
            ]

    let protocolFailure =
        ProtocolProblems.all
        |> List.filter ((<>) ProtocolProblem.InvalidScalar)
        |> List.groupBy ProtocolProblems.code
        |> List.map (fun (code, reasons) ->
            Schema.objectOf
                false
                [
                    property "protocolVersion" version true
                    property "kind" (token "protocolFailure") true
                    property "code" (token code) true
                    property
                        "diagnosticId"
                        (reasons |> List.map ProtocolProblems.token |> WireSchema.enumeration)
                        true
                    property
                        "path"
                        (Schema.string None (Some ProtocolLocation.pattern) (Some 0) (Some 256))
                        true
                ])
        |> fun variants ->
            let scalar =
                Schema.objectOf
                    false
                    [
                        property "protocolVersion" version true
                        property "kind" (token "protocolFailure") true
                        property
                            "code"
                            (token (ProtocolProblems.code ProtocolProblem.InvalidScalar))
                            true
                        property
                            "diagnosticId"
                            (token (ProtocolProblems.token ProtocolProblem.InvalidScalar))
                            true
                        property
                            "path"
                            (Schema.string None (Some ProtocolLocation.pattern) (Some 0) (Some 256))
                            true
                        property "scalarDiagnostic" RejectionDiagnosticSchemas.scalarAdmission true
                    ]

            Schema.oneOf (scalar :: variants)

    let endpointResponse projection identifier =
        [
            result projection identifier
            serviceFailure identifier
            localFailure identifier
        ]
        @ (if identifier = "recovery.export" then [ exported ] else [])
        |> Schema.oneOf

    let response projection =
        projection.CliEndpoints
        |> List.map (fun endpoint -> endpointResponse projection endpoint.Identifier)
        |> fun cases -> Schema.oneOf (protocolFailure :: cases)

    let invocationCase (endpoint: CliEndpoint) =
        let timeout =
            if endpoint.Cancellable then
                [ property "timeoutMs" (Schema.integer (Some 1L) (Some 60000L)) false ]
            else
                []

        Schema.objectOf
            false
            ([
                property "protocolVersion" version true
                property "endpoint" (token endpoint.Identifier) true
                property "input" endpoint.Input true
             ]
             @ timeout)

    let invocation projection =
        projection.CliEndpoints |> List.map invocationCase |> Schema.oneOf
