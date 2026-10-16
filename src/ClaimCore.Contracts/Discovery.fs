namespace ClaimCore.Contracts

open ClaimCore.ClientProtocol

open System.Text
open WireJson

module ContractDiscovery =
    let descriptors () = ContractDescriptors.render ()

    let catalogue () =
        obj
            [
                "wireFingerprint", text (WireContractFingerprint.value EndpointCatalog.fingerprint)
                "endpoints",
                System.Text.Json.Nodes.JsonNode.Parse(
                    CanonicalContract.bytes (EndpointCatalog.metadata ())
                )
                |> nonNull
            ]
        |> fun value ->
            value.ToJsonString(System.Text.Json.JsonSerializerOptions(WriteIndented = true))
            + "\n"
            |> Encoding.UTF8.GetBytes

    let schema name =
        match name with
        | "invocation" -> Some EndpointCatalog.invocation
        | "response" -> Some EndpointCatalog.frameResponse
        | "responses" -> Some EndpointCatalog.responses
        | "host-failure" -> Some HostFailures.schema
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

    let private guardMetadataTypeScript () =
        let json value =
            System.Text.Json.JsonSerializer.Serialize value

        let policies =
            HostFailures.all
            |> List.map (fun reason ->
                {|
                    code = HostFailures.code reason
                    status = HostFailures.status reason
                    executionPhase = HostFailures.phase reason
                    diagnosticId = HostFailures.diagnosticId reason
                |})

        "export const hostFailurePolicies = "
        + json policies
        + " as const;\n"
        + "export const caseActNames = "
        + json (
            ClaimCore.Domain.CaseActKinds.all
            |> List.map ClaimCore.Domain.CaseActKinds.token
        )
        + " as const;\n"
        + "export const authorityActNames = "
        + json (
            ClaimCore.RecordFormat.RecordedAuthorityActs.all
            |> List.map ClaimCore.RecordFormat.RecordedAuthorityActs.token
        )
        + " as const;\n"

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
                + ",acceptingScope:"
                + System.Text.Json.JsonSerializer.Serialize(e.AcceptingScope)
                + ",receiptScope:"
                + System.Text.Json.JsonSerializer.Serialize(e.ReceiptScope)
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
        + guardMetadataTypeScript ()
        + "export const strictByteProfile = " + System.Text.Json.JsonSerializer.Serialize(ProtocolProfile.strictBytes) + " as const;\n"
        + "export const resolutionEvidencePolicies = " + System.Text.Json.JsonSerializer.Serialize(DeliveryPolicy.evidencePolicies) + " as const;\n"
        + "export const contentExcludedDiagnostic = " + System.Text.Json.JsonSerializer.Serialize(DeliveryPolicy.contentExcludedDiagnostic) + " as const;\n"
        + "export const unresolvedKnowledge = " + System.Text.Json.JsonSerializer.Serialize(DeliveryPolicy.unresolvedKnowledge) + " as const;\n"
        + "export const deliveryOutcomePolicies = " + System.Text.Json.JsonSerializer.Serialize(DeliveryPolicy.outcomePolicies) + " as const;\n"
        + "export const deliveryMissingPolicies = " + System.Text.Json.JsonSerializer.Serialize(DeliveryPolicy.missingPolicies) + " as const;\n"
        + "export const deliveryHostPolicies = " + System.Text.Json.JsonSerializer.Serialize(DeliveryPolicy.hostPolicies) + " as const;\n"
        + "export const deliveryResolutionPolicies = " + System.Text.Json.JsonSerializer.Serialize(DeliveryPolicy.resolutionPolicies) + " as const;\n"

    let presentation () =
        DefaultPresentation.render () |> CanonicalContract.bytes

    let productMetadataTypeScript () =
        let quote (value: string) =
            System.Text.Json.JsonSerializer.Serialize value

        let descriptors = descriptors () |> CanonicalContract.text |> _.TrimEnd()
        let version = ClaimCore.Application.BuildIdentity.current.Version |> quote
        let fingerprint = WireContractFingerprint.value EndpointCatalog.fingerprint |> quote

        "/* Generated from ClaimCore.Contracts. Do not edit. */\nexport const productMetadata = { descriptors: "
        + descriptors
        + ", productVersion: "
        + version
        + ", wireFingerprint: "
        + fingerprint
        + " } as const;\n"
