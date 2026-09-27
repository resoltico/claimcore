namespace ClaimCore.Contracts

open System.Text.Json

module internal CliContractJson =
    let private writeEndpoint
        (writer: Utf8JsonWriter)
        (projection: ContractModel)
        (endpoint: CliEndpoint)
        =
        let shared =
            projection.WebEndpoints
            |> List.head
            |> WebSchemas.responseDocument projection
            |> _.Definitions

        let document schema =
            { TransportCatalogueWriter.document schema with
                Definitions = shared
            }

        writer.WriteStartObject()
        writer.WriteString("id", endpoint.Identifier)
        writer.WriteBoolean("cancellable", endpoint.Cancellable)
        writer.WritePropertyName("inputSchema")
        ContractJson.writeSchemaDocument writer (document endpoint.Input) endpoint.Input
        let response = CliRemoteSchemas.endpointResponse projection endpoint.Identifier
        writer.WritePropertyName("responseSchema")
        ContractJson.writeSchemaDocument writer (document response) response
        writer.WriteEndObject()

    let private write (writer: Utf8JsonWriter) (projection: ContractModel) =
        writer.WriteStartObject()
        writer.WriteString("contractKind", "CLAIMCORE_CLI_V4")

        TransportCatalogueWriter.write
            writer
            [
                "protocolFailureSchema", CliRemoteSchemas.protocolFailure
                "processFailureSchema", TransportDiagnosticSchemas.cliProcess
            ]

        writer.WriteNumber("protocolVersion", 4)
        writer.WritePropertyName("definitionSchema")

        ContractJson.writeSchemaDocument
            writer
            projection.DefinitionSchema
            projection.DefinitionSchema.Root

        writer.WritePropertyName("endpoints")
        writer.WriteStartArray()
        projection.CliEndpoints |> List.iter (writeEndpoint writer projection)
        writer.WriteEndArray()
        writer.WriteEndObject()

    let cliContract (projection: ContractModel) =
        ContractJson.contract (fun writer -> write writer projection)
