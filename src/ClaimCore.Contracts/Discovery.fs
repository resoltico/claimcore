namespace ClaimCore.Contracts

open System.Text
open WireJson

module ContractDiscovery =
    let semanticSchema () =
        ProjectionSchema.definitionDocument ClaimCore.Application.SemanticContract.current
        |> EndpointCatalog.schema

    let semantic () =
        ContractJson.semanticContract ClaimCore.Application.SemanticContract.current

    let catalogue () =
        obj
            [
                "wireFingerprint", text (WireContractFingerprint.value EndpointCatalog.fingerprint)
                "endpoints",
                (EndpointCatalog.all
                 |> List.map (fun e ->
                     obj
                         [
                             "id", text e.Identifier
                             "method", text e.Method
                             "path", text e.Path
                             "mutates", boolean e.Mutates
                             "requestSchema",
                             text ("endpoint." + e.Identifier + ".request.schema.json")
                             "responseSchema",
                             text ("endpoint." + e.Identifier + ".response.schema.json")
                             "responseDefinition", text e.Identifier
                         ])
                 |> array)
            ]
        |> fun value -> value.ToJsonString() + "\n" |> Encoding.UTF8.GetBytes

    let schema name =
        match name with
        | "invocation" -> Some EndpointCatalog.invocation
        | "response" -> Some EndpointCatalog.frameResponse
        | "responses" -> Some EndpointCatalog.responses
        | "host-failure" -> Some HostFailures.schema
        | "semantic-core" ->
            Some(ProjectionSchema.definitionDocument ClaimCore.Application.SemanticContract.current)
        | other ->
            EndpointCatalog.all
            |> List.tryPick (fun e ->
                if other = e.Identifier + ".request" then Some e.Request
                elif other = e.Identifier + ".response" then Some e.Response
                else None)
        |> Option.map EndpointCatalog.schema

    let private requestTypes () =
        EndpointCatalog.all
        |> List.map (fun e ->
            "  "
            + System.Text.Json.JsonSerializer.Serialize(e.Identifier)
            + ": "
            + Schema.typeScript e.Request.Root
            + ";")
        |> String.concat "\n"
        |> fun value -> "export type RequestByEndpoint = {\n" + value + "\n};"

    let typeScript () =
        let definitions =
            ValueSchemas.definitions
            |> List.map (fun (name, schema) ->
                "export type " + name + " = " + Schema.typeScript schema + ";")

        let responses =
            EndpointCatalog.all
            |> List.map (fun e ->
                "  "
                + System.Text.Json.JsonSerializer.Serialize(e.Identifier)
                + ": "
                + Schema.typeScript e.Response.Root
                + ";")

        String.concat
            "\n"
            ([ "/* Generated from ClaimCore.Contracts. Do not edit. */" ]
             @ definitions
             @ [
                 requestTypes ()
                 "export type ResponseByEndpoint = {"
                 String.concat "\n" responses
                 "};"
                 "export type HostFailure = " + Schema.typeScript HostFailures.schema.Root + ";"
             ])
        + "\n"

    let endpointTypeScript () =
        let endpoints =
            EndpointCatalog.all
            |> List.map (fun e ->
                "{id:"
                + System.Text.Json.JsonSerializer.Serialize(e.Identifier)
                + ",method:"
                + System.Text.Json.JsonSerializer.Serialize(e.Method)
                + ",path:"
                + System.Text.Json.JsonSerializer.Serialize(e.Path)
                + ",mutates:"
                + (if e.Mutates then "true" else "false")
                + "}")

        "/* Generated from ClaimCore.Contracts. Do not edit. */\nexport const wireContractFingerprint = "
        + System.Text.Json.JsonSerializer.Serialize(
            WireContractFingerprint.value EndpointCatalog.fingerprint
        )
        + ";\nexport const endpoints = ["
        + String.concat "," endpoints
        + "] as const;\nexport type EndpointId = (typeof endpoints)[number][\"id\"];\nexport const isEndpointId = (value: string): value is EndpointId => endpoints.some(endpoint => endpoint.id === value);\n"

    let presentation () =
        [
            yield!
                ClaimCore.Application.RejectionDiagnosticIds.all
                |> List.map (fun (reason, id) ->
                    "diagnostic." + id,
                    (RejectionPresentation.template reason
                     |> List.map (function
                         | DiagnosticTextPart.Literal value -> Map.ofList [ "literal", value ]
                         | DiagnosticTextPart.Hole hole ->
                             Map.ofList [ "hole", RejectionPresentation.holeName hole ])))
            yield!
                ClaimCore.Application.CoreFaults.all
                |> List.map (fun (reason, id) ->
                    "diagnostic." + id,
                    [ Map.ofList [ "literal", CoreFaultPresentation.render reason ] ])
            yield!
                HostFailures.all
                |> List.map (fun reason ->
                    "diagnostic." + HostFailures.diagnosticId reason,
                    [ Map.ofList [ "literal", HostFailures.render reason ] ])
        ]
        |> Map.ofList
        |> System.Text.Json.JsonSerializer.SerializeToUtf8Bytes

    let productMetadataTypeScript () =
        let quote value =
            System.Text.Json.JsonSerializer.Serialize(value: string)

        let definition = semantic () |> CanonicalContract.text |> _.TrimEnd()
        let version = ClaimCore.Application.BuildIdentity.current.Version |> quote

        let semanticFingerprint =
            ClaimCore.Application.SemanticContract.fingerprint
                ClaimCore.Application.SemanticContract.current
            |> ClaimCore.Application.SemanticCoreFingerprint.value
            |> quote

        let fingerprint = WireContractFingerprint.value EndpointCatalog.fingerprint |> quote

        "/* Generated from ClaimCore.Contracts. Do not edit. */\nimport type { SemanticDefinition } from \"./types\";\nexport const productMetadata = { definition: "
        + definition
        + ", productVersion: "
        + version
        + ", semanticFingerprint: "
        + semanticFingerprint
        + ", wireFingerprint: "
        + fingerprint
        + " } as const satisfies {definition: SemanticDefinition; productVersion: string; semanticFingerprint: string; wireFingerprint: string};\n"
