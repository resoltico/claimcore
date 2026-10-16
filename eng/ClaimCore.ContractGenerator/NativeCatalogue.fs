namespace ClaimCore.ContractGeneration

open System
open System.Text
open System.Text.Json
open ClaimCore.Contracts
open ClaimCore.ClientProtocol

module NativeCatalogue =
    let private quote (value: string) = JsonSerializer.Serialize value

    let private bytes value =
        "Convert.FromBase64String(" + quote (Convert.ToBase64String value) + ")"

    let private schema value =
        "SchemaDocuments.decode ("
        + bytes (EndpointCatalog.schema value |> CanonicalContract.bytes)
        + ")"

    let private validateOutcomes (endpoints: Endpoint list) =
        for endpoint in endpoints do
            SchemaSupport.document endpoint.Request
            SchemaSupport.document endpoint.Response

            for bytes in SchemaSamples.variants endpoint.Response do
                use document = JsonDocument.Parse(ReadOnlyMemory bytes)

                let tag =
                    document.RootElement.GetProperty("outcome").GetProperty("tag").GetString()
                    |> nonNull

                DeliveryPolicy.outcome endpoint.Mutates tag |> ignore

    let private endpointValues (endpoints: Endpoint list) =
        endpoints
        |> List.map (fun endpoint ->
            let scope value =
                if value = "NONE" then "None" else "Some " + quote value

            "{ Identifier = "
            + quote endpoint.Identifier
            + "; Method = "
            + quote endpoint.Method
            + "; Path = "
            + quote endpoint.Path
            + "; Mutates = "
            + (if endpoint.Mutates then "true" else "false")
            + "; AcceptingScope = "
            + (scope endpoint.AcceptingScope)
            + "; ReceiptScope = "
            + scope endpoint.ReceiptScope
            + "; Request = "
            + schema endpoint.Request
            + "; Response = "
            + schema endpoint.Response
            + " }")

    let private catalogueLines namespaceName (endpoints: Endpoint list) =
        [
            "// Generated from the owned contract definitions. Do not edit."
            "namespace " + namespaceName
            "open System"
            "open ClaimCore.ClientProtocol"
            "module EndpointCatalog ="
            "    let all : ClientEndpoint list = [ "
            + String.concat ";\n        " (endpointValues endpoints)
            + " ]"
            "    let fingerprint = WireContractFingerprint.create "
            + quote (WireContractFingerprint.value EndpointCatalog.fingerprint)
            "    let tryFind id = all |> List.tryFind (fun endpoint -> endpoint.Identifier = id)"
            "    let invocation = " + schema (EndpointCatalog.invocationFor endpoints)
            "    let frameResponse = " + schema (EndpointCatalog.frameResponseFor endpoints)
            "    let catalogue = { Endpoints = all; Invocation = invocation; FrameResponse = frameResponse }"
        ]

    let private frameLines =
        [
            "module Frames ="
            "    let parse bytes = ProtocolFrames.parse EndpointCatalog.catalogue bytes"
            "    let endpoint = ProtocolFrames.endpoint"
            "    let input = ProtocolFrames.input"
            "    let bytes = ProtocolFrames.bytes"
            "    let inputBytes = ProtocolFrames.inputBytes"
            "    let decode bytes = ProtocolFrames.decode EndpointCatalog.catalogue bytes"
            "    let response = ProtocolFrames.response"
            "    let result = ProtocolFrames.result"
            "    let serviceFailure = ProtocolFrames.serviceFailure"
            "    let localFailure code = ProtocolFrames.failure \"localFailure\" code"
            "    let protocolFailure code = ProtocolFrames.failure \"protocolFailure\" code"
            "    let exitCode = ProtocolFrames.exitCode"
        ]

    let private discoveryLines (endpoints: Endpoint list) descriptors catalogue =
        [
            "module ContractDiscovery ="
            "    let descriptors () = " + bytes descriptors
            "    let catalogue () = " + bytes catalogue
            "    let presentation () = " + bytes (ContractDiscovery.presentation ())
            "    let schema name ="
            "        match name with"
            yield!
                ([
                    "invocation", EndpointCatalog.invocationFor endpoints
                    "response", EndpointCatalog.frameResponseFor endpoints
                    "responses", EndpointCatalog.responsesFor endpoints
                    "host-failure", HostFailures.schema
                 ]
                 @ (endpoints
                    |> List.collect (fun endpoint ->
                        [
                            endpoint.Identifier + ".request", endpoint.Request
                            endpoint.Identifier + ".response", endpoint.Response
                        ])))
                |> List.map (fun (name, document) ->
                    "        | "
                    + quote name
                    + " -> Some("
                    + bytes (EndpointCatalog.schema document |> CanonicalContract.bytes)
                    + ")")
            "        | _ -> None"
            ""
        ]

    let sourceFor namespaceName endpoints descriptors catalogue =
        validateOutcomes endpoints

        catalogueLines namespaceName endpoints
        @ frameLines
        @ discoveryLines endpoints descriptors catalogue
        |> String.concat "\n"
        |> Encoding.UTF8.GetBytes

    let render () =
        sourceFor
            "ClaimCore.ClientContract"
            EndpointCatalog.all
            (ContractDiscovery.descriptors () |> CanonicalContract.bytes)
            (ContractDiscovery.catalogue ())
