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

    let renderGroup name (endpoints: WebEndpoint list) =
        let body = endpoints |> List.map endpoint |> String.concat "\n"

        $"""/* Generated from ClaimCore.Contracts. Do not edit. */
export const {name} = [
{body}
] as const;
"""

    let renderIndex fingerprint =

        let statuses =
            WebSchemaDefinitions.hostFailureStatuses
            |> List.map string
            |> String.concat ", "

        $"""/* Generated from ClaimCore.Contracts. Do not edit. */
import {{ webV3CaseworkEndpoints }} from "./web-v3.endpoint-catalog.casework";
import {{ webV3AuthorityEndpoints }} from "./web-v3.endpoint-catalog.authority";

export const webV3WireContractFingerprint =
  {stringLiteral fingerprint};

export const webV3TransportLimits = {{
  jsonResponseBytes: {TransportLimits.JsonResponseBytes},
  recoveryArtifactBytes: {TransportLimits.RecoveryArtifactBytes},
}} as const;

export const webV3HostFailureStatuses = [{statuses}] as const;

export const webV3Endpoints = [...webV3CaseworkEndpoints, ...webV3AuthorityEndpoints] as const;

export type WebV3EndpointId = (typeof webV3Endpoints)[number]["id"];

export const isWebV3EndpointId = (value: string): value is WebV3EndpointId =>
  webV3Endpoints.some((endpoint) => endpoint.id === value);
"""

module ContractArtifacts =
    let private artifact name (contract: CanonicalContract) =
        {
            Name = name
            Bytes = CanonicalContract.bytes contract
        }

    let private typeScriptArtifacts projection fingerprint =
        let authority (endpoint: WebEndpoint) =
            endpoint.Identifier.StartsWith("authority.", StringComparison.Ordinal)
            || endpoint.Identifier.StartsWith("lifecycle.", StringComparison.Ordinal)
            || endpoint.Identifier.StartsWith("tombstone.", StringComparison.Ordinal)

        let casework, administration =
            projection.WebEndpoints |> List.partition (authority >> not)

        if
            casework.IsEmpty
            || administration.IsEmpty
            || (casework @ administration |> List.map _.Identifier)
               <> (projection.WebEndpoints |> List.map _.Identifier)
        then
            invalidOp "Web endpoint catalog groups must preserve exact endpoint order."

        [
            {
                Name = "web-v3.endpoint-catalog.ts"
                Bytes = TypeScriptCatalog.renderIndex fingerprint |> Encoding.UTF8.GetBytes
            }
            {
                Name = "web-v3.endpoint-catalog.casework.ts"
                Bytes =
                    TypeScriptCatalog.renderGroup "webV3CaseworkEndpoints" casework
                    |> Encoding.UTF8.GetBytes
            }
            {
                Name = "web-v3.endpoint-catalog.authority.ts"
                Bytes =
                    TypeScriptCatalog.renderGroup "webV3AuthorityEndpoints" administration
                    |> Encoding.UTF8.GetBytes
            }
        ]

    let private webTypesArtifacts projection =
        WebTypeScript.artifacts projection
        |> List.map (fun (name, bytes) -> { Name = name; Bytes = bytes })

    let private cliBaseArtifacts projection =
        [
            artifact "semantic-core-v1.contract.json" (ContractRenderers.semantic projection)
            artifact "semantic-core-v1.schema.json" (ContractRenderers.semanticSchema projection)
            artifact "cli-v4.catalog.json" (ContractRenderers.cli projection)
            artifact "cli-v4.invocation.schema.json" (CliSchemas.invocation projection)
            artifact "cli-v4.response.schema.json" (CliSchemas.response projection)
            artifact "cli-v4.definition.schema.json" (CliSchemas.definition projection)
            artifact "recovery-artifact-v3.schema.json" (CliSchemas.recoveryEnvelope projection)
        ]

    let private cliEndpointArtifacts projection =
        projection.CliEndpoints
        |> List.map (fun endpoint ->
            let schema =
                CliSchemas.endpoint projection endpoint.Identifier
                |> Option.defaultWith (fun () -> invalidOp "CLI endpoint schema is missing.")

            artifact ("cli-v4.endpoint." + endpoint.Identifier + ".schema.json") schema)

    let private cliEndpointResponseArtifacts projection =
        projection.CliEndpoints
        |> List.map (fun endpoint ->
            let schema =
                CliSchemas.endpointResponse projection endpoint.Identifier
                |> Option.defaultWith (fun () ->
                    invalidOp "CLI endpoint response schema is missing.")

            artifact ("cli-v4.endpoint." + endpoint.Identifier + ".response.schema.json") schema)

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
            ProcessDiagnosticCorpus.artifact ()
        ]
        |> List.map (fun (name, bytes) -> { Name = name; Bytes = bytes })

    let all projection =
        let web = ContractRenderers.web projection

        cliBaseArtifacts projection
        @ cliEndpointArtifacts projection
        @ cliEndpointResponseArtifacts projection
        @ [
            {
                Name = "default-presentation.en.json"
                Bytes = DefaultPresentation.bytes ()
            }
            {
                Name = "web-v3.product-metadata.ts"
                Bytes = ProductMetadataArtifact.bytes projection
            }
            artifact "web-v3.catalog.json" web
            artifact
                "cli-v4.process-failure.schema.json"
                TransportDiagnosticSchemas.cliProcessDocument
            artifact
                "web-v3.process-failure.schema.json"
                TransportDiagnosticSchemas.webStartupDocument
            {
                Name = "administration-v1.response.schema.json"
                Bytes = ClaimCore.Database.DatabaseContracts.schema ()
            }
            {
                Name = "administration-v1.catalog.json"
                Bytes = ClaimCore.Database.DatabaseContracts.catalogue ()
            }
            artifact "web-v3.host-failure.schema.json" WebSchemas.hostFailure
            artifact "web-v3.responses.schema.json" (WebSchemas.responses projection)
        ]
        @ typeScriptArtifacts
            projection
            (ContractRenderers.webFingerprint projection |> WebWireContractFingerprint.value)
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
