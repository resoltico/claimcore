namespace ClaimCore.Contracts

open WireSchema

module internal FrameSchemas =
    let localCodes =
        [
            "CLI_CONFIGURATION_INVALID"
            "CLI_AUTHENTICATION_FAILED"
            "CLI_SERVICE_UNAVAILABLE"
            "CLI_CANCELLED"
            "CLI_INTERNAL"
        ]

    let protocolCodes = [ "INVALID_FRAME"; "INVALID_RESPONSE"; "CONTRACT_MISMATCH" ]

    let private localFailure =
        objectOf
            [
                property "kind" (token "localFailure")
                property "code" (enumeration localCodes)
            ]

    let private protocolFailure =
        objectOf
            [
                property "kind" (token "protocolFailure")
                property "code" (enumeration protocolCodes)
                property "diagnosticId" (token "PROTOCOL_INVALID_ENVELOPE")
                property
                    "path"
                    (Schema.string None (Some "^/(?:input|outcome|endpoint)?$") (Some 1) (Some 9))
            ]

    let response entries =
        let results =
            entries
            |> List.map (fun (id, _, response) ->
                objectOf
                    [
                        property "kind" (token "result")
                        property "endpoint" (token id)
                        property "outcome" response
                    ])

        let failures =
            entries
            |> List.map (fun (id, _, _) ->
                objectOf
                    [
                        property "kind" (token "serviceFailure")
                        property "endpoint" (token id)
                        property
                            "failure"
                            (objectOf
                                [
                                    property
                                        "code"
                                        (HostFailures.all
                                         |> List.map HostFailures.code
                                         |> List.distinct
                                         |> enumeration)
                                ])
                    ])

        Schema.oneOf (results @ failures @ [ localFailure; protocolFailure ])
