namespace ClaimCore.ContractGeneration

open ClaimCore.Contract

module ContractArtifacts =
    let all () =
        CatalogValidation.validate ()
        BindingValidation.validate CatalogEntries.all

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
                Name = "catalog.json"
                Bytes = ContractDocuments.catalog ()
            }
        ]
