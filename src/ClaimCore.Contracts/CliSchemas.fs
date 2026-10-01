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

    let private renderRemote (projection: ContractModel) identifier title root =
        let definitions =
            projection.WebEndpoints
            |> List.head
            |> WebSchemas.responseDocument projection
            |> _.Definitions

        {
            Identifier = identifier
            Title = title
            Root = root
            Definitions = definitions
        }
        |> fun value -> CanonicalJson.renderSchema value value.Root

    let private invocationCase (endpoint: CliEndpoint) =
        CliRemoteSchemas.invocationCase endpoint

    let invocation (projection: ContractModel) =
        projection.CliEndpoints
        |> List.map invocationCase
        |> Schema.oneOf
        |> renderRemote
            projection
            "https://claimcore.local/contracts/cli-v4.invocation.schema.json"
            "ClaimCore CLI v4 invocation"

    let responseDocument (projection: ContractModel) =
        let definitions =
            projection.WebEndpoints
            |> List.head
            |> WebSchemas.responseDocument projection
            |> _.Definitions

        {
            Identifier = "https://claimcore.local/contracts/cli-v4.response.schema.json"
            Title = "ClaimCore CLI v4 response"
            Root = CliRemoteSchemas.response projection
            Definitions = definitions
        }

    let response projection =
        let document = responseDocument projection
        CanonicalJson.renderSchema document document.Root

    let definition (projection: ContractModel) =
        CanonicalJson.renderSchema projection.DefinitionSchema projection.DefinitionSchema.Root

    let recoveryEnvelope _ =
        Schema.objectOf
            false
            [
                Schema.property
                    "format"
                    (Schema.constant (TextConstant "claimcore-recovery-artifact"))
                    true
                Schema.property "formatVersion" (Schema.constant (IntegerConstant 3L)) true
                Schema.property "keyId" ScalarSchemas.uuid true
                Schema.property "exportId" ScalarSchemas.uuid true
                Schema.property "installationId" ScalarSchemas.uuid true
                Schema.property "epoch" (Schema.integer (Some 1L) None) true
                Schema.property "caseId" ScalarSchemas.uuid true
                Schema.property "preparerActorId" ScalarSchemas.uuid true
                Schema.property "preparerGrantRevision" (Schema.integer (Some 0L) None) true
                Schema.property "importerActorId" (Schema.nullable ScalarSchemas.uuid) true
                Schema.property "exporterActorId" ScalarSchemas.uuid true
                Schema.property "exporterGrantRevision" (Schema.integer (Some 0L) None) true
                Schema.property "operationId" ScalarSchemas.uuid true
                Schema.property "issuedAt" WireSchema.timestamp true
                Schema.property "expiresAt" WireSchema.timestamp true
                Schema.property "canonicalCommandFormat" (Schema.constant (IntegerConstant 3L)) true
                Schema.property
                    "requestFingerprintVersion"
                    (Schema.constant (IntegerConstant 1L))
                    true
                Schema.property "nonceBase64" (Schema.string None None (Some 16) (Some 16)) true
                Schema.property
                    "ciphertextBase64"
                    (Schema.string None None (Some 4) (Some 131072))
                    true
                Schema.property "tagBase64" (Schema.string None None (Some 24) (Some 24)) true
                Schema.property "macSha256" EndpointInputs.digest true
            ]
        |> render
            "https://claimcore.local/contracts/recovery-artifact-v3.schema.json"
            "ClaimCore encrypted recovery artifact v3"

    let endpoint (projection: ContractModel) identifier =
        projection.CliEndpoints
        |> List.tryFind (fun item -> item.Identifier = identifier)
        |> Option.map (fun item ->
            renderRemote
                projection
                ("https://claimcore.local/contracts/cli-v4." + item.Identifier + ".schema.json")
                ("ClaimCore CLI v4 " + item.Identifier + " input")
                item.Input)

    let endpointResponse (projection: ContractModel) identifier =
        projection.CliEndpoints
        |> List.tryFind (fun endpoint -> endpoint.Identifier = identifier)
        |> Option.map (fun _ ->
            CliRemoteSchemas.endpointResponse projection identifier
            |> renderRemote
                projection
                ("https://claimcore.local/contracts/cli-v4.endpoint."
                 + identifier
                 + ".response.schema.json")
                ("ClaimCore CLI v4 " + identifier + " response"))
