namespace ClaimCore.Contracts

module CliSchemas =
    let private document identifier title root =
        {
            Identifier = identifier
            Title = title
            Root = root
            Definitions = []
        }

    let private render identifier title root =
        document identifier title root
        |> fun value -> CanonicalJson.renderSchema value value.Root

    let private invocationCase (endpoint: CliEndpoint) =
        Schema.objectOf
            false
            [
                Schema.property "protocolVersion" (Schema.constant (IntegerConstant 3L)) true
                Schema.property "endpoint" (Schema.constant (TextConstant endpoint.Identifier)) true
                Schema.property "input" endpoint.Input true
                Schema.property "timeoutMs" (Schema.integer (Some 1L) (Some 60000L)) false
            ]

    let invocation (projection: ContractModel) =
        projection.CliEndpoints
        |> List.map invocationCase
        |> Schema.oneOf
        |> render
            "https://claimcore.local/contracts/cli-v3.invocation.schema.json"
            "ClaimCore CLI v3 invocation"

    let private resultEnvelope endpoint outcome =
        Schema.objectOf
            false
            [
                Schema.property "protocolVersion" (WireSchema.number 3) true
                Schema.property "kind" (WireSchema.token "result") true
                Schema.property "endpoint" (WireSchema.token endpoint) true
                Schema.property "outcome" outcome true
            ]

    let response projection =
        let results =
            projection.CliEndpoints
            |> List.map (fun endpoint ->
                resultEnvelope
                    endpoint.Identifier
                    (Map.find endpoint.Identifier projection.CliResponses))

        Schema.oneOf (CliResponseSchemas.protocolFailure :: results)
        |> render
            "https://claimcore.local/contracts/cli-v3.response.schema.json"
            "ClaimCore CLI v3 response"

    let definition (projection: ContractModel) =
        CanonicalJson.renderSchema projection.DefinitionSchema projection.DefinitionSchema.Root

    let recoveryEnvelope _ =
        Schema.objectOf
            false
            [
                Schema.property "format" (Schema.constant (TextConstant "claimcore-recovery")) true
                Schema.property "formatVersion" (Schema.constant (IntegerConstant 1L)) true
                Schema.property "installationId" ScalarSchemas.uuid true
                Schema.property "operationId" ScalarSchemas.uuid true
                Schema.property "protocolVersion" (Schema.constant (IntegerConstant 2L)) true
                Schema.property
                    "requestFingerprintVersion"
                    (Schema.constant (IntegerConstant 1L))
                    true
                Schema.property "requestSha256" EndpointInputs.digest true
                Schema.property
                    "canonicalRequestBase64"
                    (Schema.string None None (Some 4) None)
                    true
            ]
        |> render
            "https://claimcore.local/contracts/recovery-envelope-v1.schema.json"
            "ClaimCore recovery envelope v1"

    let endpoint (projection: ContractModel) identifier =
        projection.CliEndpoints
        |> List.tryFind (fun item -> item.Identifier = identifier)
        |> Option.map (fun item ->
            render
                ("https://claimcore.local/contracts/cli-v3." + item.Identifier + ".schema.json")
                ("ClaimCore CLI v3 " + item.Identifier + " input")
                item.Input)

    let endpointResponse (projection: ContractModel) identifier =
        projection.CliResponses
        |> Map.tryFind identifier
        |> Option.map (fun outcome ->
            resultEnvelope identifier outcome
            |> render
                ("https://claimcore.local/contracts/cli-v3.endpoint."
                 + identifier
                 + ".response.schema.json")
                ("ClaimCore CLI v3 " + identifier + " response"))
