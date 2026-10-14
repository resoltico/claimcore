namespace ClaimCore.ContractGeneration

open System.Text
open System.Text.Json.Nodes
open ClaimCore.Contracts

type Artifact = { Name: string; Bytes: byte array }

module ContractArtifacts =
    let private schema name document =
        {
            Name = name
            Bytes = EndpointCatalog.schema document |> CanonicalContract.bytes
        }

    let all () =
        let endpoints =
            EndpointCatalog.all
            |> List.collect (fun e ->
                [
                    schema ("endpoint." + e.Identifier + ".request.schema.json") e.Request
                    schema ("endpoint." + e.Identifier + ".response.schema.json") e.Response
                ])

        endpoints
        @ [
            {
                Name = "default-presentation.en.json"
                Bytes = ContractDiscovery.presentation ()
            }
            {
                Name = "product-metadata.ts"
                Bytes = ContractDiscovery.productMetadataTypeScript () |> Encoding.UTF8.GetBytes
            }
            schema "invocation.schema.json" EndpointCatalog.invocation
            schema "frame-response.schema.json" EndpointCatalog.frameResponse
            schema "responses.schema.json" EndpointCatalog.responses
            schema "host-failure.schema.json" HostFailures.schema
            {
                Name = "semantic-core.schema.json"
                Bytes = ContractDiscovery.semanticSchema () |> CanonicalContract.bytes
            }
            {
                Name = "semantic-core.contract.json"
                Bytes = ContractDiscovery.semantic () |> CanonicalContract.bytes
            }
            {
                Name = "catalog.json"
                Bytes = ContractDiscovery.catalogue ()
            }
            {
                Name = "types.ts"
                Bytes = ContractDiscovery.typeScript () |> Encoding.UTF8.GetBytes
            }
            {
                Name = "endpoint-catalog.ts"
                Bytes = ContractDiscovery.endpointTypeScript () |> Encoding.UTF8.GetBytes
            }
            {
                Name = "endpoint-corpus.json"
                Bytes = Corpus.bytes ()
            }
        ]

    let manifest artifacts =
        let files = JsonArray()
        artifacts |> List.iter (fun value -> files.Add(JsonValue.Create value.Name))
        let result = JsonObject()
        result.Add("generator", JsonValue.Create "ClaimCore.Contracts")

        result.Add(
            "wireFingerprint",
            JsonValue.Create(WireContractFingerprint.value EndpointCatalog.fingerprint)
        )

        result.Add("files", files)
        result.ToJsonString() + "\n" |> Encoding.UTF8.GetBytes
