namespace ClaimCore.ContractGeneration

open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation

open ClaimCore.Json

module ContractArtifacts =
    let all () =
        let catalogs = CatalogValidation.validate ()

        [
            {
                Name = "legal.ts"
                Bytes =
                    System.Text.Encoding.UTF8.GetBytes(
                        "// Generated from Core legal metadata.\nexport const legal = "
                        + System.Text.Json.JsonSerializer.Serialize(
                            {|
                                copyright = LegalNotice.copyright
                                rights = LegalNotice.rights
                                license = LegalNotice.license
                                licenseFile = LegalNotice.licenseFile
                                sourceFile = LegalNotice.sourceFile
                                projectNotice = LegalNotice.projectNotice
                                commercialNotice = LegalNotice.commercialNotice
                            |}
                        )
                        + " as const;\n"
                    )
            }
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
