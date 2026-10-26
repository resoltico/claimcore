namespace ClaimCore.ContractGeneration

open ClaimCore.Contract

module ContractArtifacts =
    let all () =
        let catalogs = CatalogValidation.validate ()

        [
            {
                Name = "openapi.json"
                Bytes = Array.append (WireWriter.indentedBytes OpenApi.document) [| byte '\n' |]
            }
            {
                Name = "client.ts"
                Bytes = TypeScript.source ContractIdentity.value
            }
            {
                Name = "messages.ts"
                Bytes = MessageGeneration.source catalogs
            }
            {
                Name = "catalog.json"
                Bytes = ContractDocuments.catalog ()
            }
        ]
