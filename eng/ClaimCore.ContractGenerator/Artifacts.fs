namespace ClaimCore.ContractGeneration

open System
open System.Buffers
open System.Text
open System.Text.Json
open ClaimCore.Application
open ClaimCore.Contracts

type Artifact = { Name: string; Bytes: byte array }

module private TypeScriptCatalog =
    let private stringLiteral value = JsonSerializer.Serialize(value)

    let private rawBody mediaType maximumBytes headers =
        let headerNames =
            headers |> List.map fst |> List.map stringLiteral |> String.concat ", "

        [
            "kind: \"RAW\","
            $"mediaType: {stringLiteral mediaType},"
            $"maximumBytes: {maximumBytes},"
            $"requiredHeaders: [{headerNames}],"
        ]

    let private simpleBody body =
        match body with
        | None -> "null"
        | Some(JsonBody _) -> "{ kind: \"JSON\" }"
        | Some(RawBody _) -> invalidOp "Raw bodies require multiline TypeScript rendering."

    let private endpoint (value: WebEndpoint) =
        let identifier = stringLiteral value.Identifier
        let method = stringLiteral value.Method
        let path = stringLiteral value.Path
        let responseSchema = WebSchemas.responseFileName value.Identifier |> stringLiteral

        let responseDefinition =
            WebSchemas.responseDefinitionName value.Identifier |> stringLiteral

        let successMediaType =
            value.SuccessMediaType |> Option.map stringLiteral |> Option.defaultValue "null"

        match value.Body with
        | Some(RawBody(mediaType, maximumBytes, headers)) ->
            [
                "  {"
                $"    id: {identifier},"
                $"    method: {method},"
                $"    path: {path},"
                "    body: {"
                rawBody mediaType maximumBytes headers
                |> List.map (fun line -> "      " + line)
                |> String.concat "\n"
                "    },"
                $"    responseSchema: {responseSchema},"
                $"    responseDefinition: {responseDefinition},"
                $"    successMediaType: {successMediaType},"
                "  },"
            ]
            |> String.concat "\n"
        | body ->
            [
                "  {"
                $"    id: {identifier},"
                $"    method: {method},"
                $"    path: {path},"
                $"    body: {simpleBody body},"
                $"    responseSchema: {responseSchema},"
                $"    responseDefinition: {responseDefinition},"
                $"    successMediaType: {successMediaType},"
                "  },"
            ]
            |> String.concat "\n"

    let render fingerprint (endpoints: WebEndpoint list) =
        let body = endpoints |> List.map endpoint |> String.concat "\n"

        let statuses =
            WebSchemaDefinitions.hostFailureStatuses
            |> List.map string
            |> String.concat ", "

        $"""/* Generated from ClaimCore.Contracts. Do not edit. */
export const webV2WireContractFingerprint =
  {stringLiteral fingerprint};

export const webV2HostFailureStatuses = [{statuses}] as const;

export const webV2Endpoints = [
{body}
] as const;

export type WebV2EndpointId = (typeof webV2Endpoints)[number]["id"];

export const isWebV2EndpointId = (value: string): value is WebV2EndpointId =>
  webV2Endpoints.some((endpoint) => endpoint.id === value);
"""

module ContractArtifacts =
    let private artifact name (contract: CanonicalContract) =
        {
            Name = name
            Bytes = CanonicalContract.bytes contract
        }

    let private typeScriptArtifact projection fingerprint =
        let text = TypeScriptCatalog.render fingerprint projection.WebEndpoints

        {
            Name = "web-v2.endpoint-catalog.ts"
            Bytes = Encoding.UTF8.GetBytes(text)
        }

    let private webTypesArtifacts projection =
        WebTypeScript.artifacts projection
        |> List.map (fun (name, bytes) -> { Name = name; Bytes = bytes })

    let private cliBaseArtifacts projection =
        [
            artifact "semantic-core-v1.contract.json" (ContractRenderers.semantic projection)
            artifact "semantic-core-v1.schema.json" (ContractRenderers.semanticSchema projection)
            artifact "cli-v3.catalog.json" (ContractRenderers.cli projection)
            artifact "cli-v3.invocation.schema.json" (CliSchemas.invocation projection)
            artifact "cli-v3.response.schema.json" (CliSchemas.response projection)
            artifact "cli-v3.definition.schema.json" (CliSchemas.definition projection)
            artifact "recovery-envelope-v1.schema.json" (CliSchemas.recoveryEnvelope projection)
        ]

    let private cliEndpointArtifacts projection =
        projection.CliEndpoints
        |> List.map (fun endpoint ->
            let schema =
                CliSchemas.endpoint projection endpoint.Identifier
                |> Option.defaultWith (fun () -> invalidOp "CLI endpoint schema is missing.")

            artifact ("cli-v3.endpoint." + endpoint.Identifier + ".schema.json") schema)

    let private cliEndpointResponseArtifacts projection =
        projection.CliEndpoints
        |> List.map (fun endpoint ->
            let schema =
                CliSchemas.endpointResponse projection endpoint.Identifier
                |> Option.defaultWith (fun () ->
                    invalidOp "CLI endpoint response schema is missing.")

            artifact ("cli-v3.endpoint." + endpoint.Identifier + ".response.schema.json") schema)

    let private webResponseArtifacts projection =
        projection.WebEndpoints
        |> List.map (fun endpoint ->
            artifact
                (WebSchemas.responseFileName endpoint.Identifier)
                (WebSchemas.response projection endpoint))

    let private corpusArtifacts projection =
        [
            CliResponseCorpus.artifact projection
            CliRawCorpus.artifact projection
            WebParsedCorpus.artifact projection
        ]
        |> List.map (fun (name, bytes) -> { Name = name; Bytes = bytes })

    let all projection =
        let web = ContractRenderers.web projection

        cliBaseArtifacts projection
        @ cliEndpointArtifacts projection
        @ cliEndpointResponseArtifacts projection
        @ [
            artifact "web-v2.catalog.json" web
            artifact "web-v2.host-failure.schema.json" WebSchemas.hostFailure
            artifact "web-v2.responses.schema.json" (WebSchemas.responses projection)
            typeScriptArtifact
                projection
                (ContractRenderers.webFingerprint projection |> WebWireContractFingerprint.value)
        ]
        @ webTypesArtifacts projection
        @ webResponseArtifacts projection
        @ corpusArtifacts projection

    let manifest artifacts projection =
        let semantic =
            ContractRenderers.semanticFingerprint projection
            |> SemanticCoreFingerprint.value

        let cli =
            ContractRenderers.cliFingerprint projection |> CliWireContractFingerprint.value

        let web =
            ContractRenderers.webFingerprint projection |> WebWireContractFingerprint.value

        let names = artifacts |> List.map _.Name |> List.toArray

        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer)
        writer.WriteStartObject()
        writer.WriteString("generator", "ClaimCore.Contracts")
        writer.WriteString("semanticFingerprint", semantic)
        writer.WriteString("cliWireContractFingerprint", cli)
        writer.WriteString("webWireContractFingerprint", web)
        writer.WriteStartArray("files")
        names |> Array.iter writer.WriteStringValue
        writer.WriteEndArray()
        writer.WriteEndObject()
        writer.Flush()
        Array.append (buffer.WrittenSpan.ToArray()) [| byte '\n' |]
