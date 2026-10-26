namespace ClaimCore.ContractGeneration

open ClaimCore.Contract
open ClaimCore.Json

module ContractCatalog =
    let renderFrom (document: CatalogDocument) =
        document
        |> CatalogCodecs.codec.Write
        |> WireWriter.indentedBytes
        |> fun bytes -> Array.append bytes [| byte '\n' |]

    let render () = renderFrom Catalog.document
