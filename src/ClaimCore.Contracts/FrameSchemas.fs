namespace ClaimCore.Contracts

open ClaimCore.ClientProtocol

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

    let private delivery =
        [ property "operationId" (Schema.nullable ScalarSchemas.uuid)
          property "attemptKnowledge" (enumeration [ "NOT_SENT"; "READ_SUCCEEDED"; "READ_FAILED"; "ACCEPTED"; "NOT_ACCEPTED"; "UNCONFIRMED" ])
          property "resolutionEvidence" (enumeration [ "NONE"; "CONTENT_ACCEPTED"; "CONTENT_EXCLUDED" ]) ]

    let private delivered schema =
        match schema with
        | ObjectSchema value ->
            let properties = value.Properties |> List.filter (fun item -> item.Name <> "diagnosticId" && item.Name <> "path")
            let endpoint = if properties |> List.exists (fun item -> item.Name = "endpoint") then [] else [ property "endpoint" (Schema.nullable text) ]
            Schema.objectOf false (properties @ endpoint @ delivery)
        | _ -> invalidArg "schema" "A frame must be an object."

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
                            HostFailures.schema.Root
                    ])

        results @ failures @ [ localFailure; protocolFailure ] |> List.map delivered |> Schema.oneOf
