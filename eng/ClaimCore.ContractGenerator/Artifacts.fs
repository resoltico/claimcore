namespace ClaimCore.ContractGeneration

open ClaimCore.ClientProtocol

open System.Text
open System.Text.Json.Nodes
open ClaimCore.Contracts

module ContractArtifacts =
    let private schema name document =
        {
            Name = name
            Bytes = EndpointCatalog.schema document |> CanonicalContract.bytes
        }

    let private endpointSchemas () =
        EndpointCatalog.all
        |> List.collect (fun endpoint ->
            [
                schema
                    ("endpoint." + endpoint.Identifier + ".request.schema.json")
                    endpoint.Request
                schema
                    ("endpoint." + endpoint.Identifier + ".response.schema.json")
                    endpoint.Response
            ])

    let private clientArtifacts () =
        [
            {
                Name = "ContractExtensionFixture.fs"
                Bytes = ContractExtension.native ()
            }
            {
                Name = "extension-fixture.ts"
                Bytes = ContractExtension.browser ()
            }
            {
                Name = "extension-corpus.json"
                Bytes = Corpus.forEndpoints ContractExtension.endpoints
            }
            schema "extension.request.schema.json" ContractExtension.request
            {
                Name = "request-builders.ts"
                Bytes = BrowserBuilders.source ()
            }
            {
                Name = "browser-contract.ts"
                Bytes = BrowserArtifacts.source ()
            }
            {
                Name = "strict-byte-profile.json"
                Bytes = ProtocolProfile.strictBytesJson ()
            }
            {
                Name = "delivery-policy.json"
                Bytes = ProtocolProfile.deliveryJson ()
            }
            {
                Name = "ClientContract.fs"
                Bytes = NativeCatalogue.render ()
            }
        ]

    let private catalogueArtifacts () =
        [
            {
                Name = "default-presentation.en.json"
                Bytes = ContractDiscovery.presentation ()
            }
            {
                Name = "product-metadata.ts"
                Bytes = ContractDiscovery.productMetadataTypeScript () |> Encoding.UTF8.GetBytes
            }
            {
                Name = "descriptors.json"
                Bytes = ContractDiscovery.descriptors () |> CanonicalContract.bytes
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

    let private protocolSchemas () =
        [
            schema "invocation.schema.json" EndpointCatalog.invocation
            schema "frame-response.schema.json" EndpointCatalog.frameResponse
            schema "responses.schema.json" EndpointCatalog.responses
            schema "host-failure.schema.json" HostFailures.schema
        ]

    let all () =
        endpointSchemas ()
        @ clientArtifacts ()
        @ catalogueArtifacts ()
        @ protocolSchemas ()
        |> ArtifactPublication.withInventory
