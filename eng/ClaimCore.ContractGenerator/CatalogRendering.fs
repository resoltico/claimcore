namespace ClaimCore.ContractGeneration

open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation

open ClaimCore.Json

module ContractCatalog =
    let renderFrom (document: CatalogDocument) =
        document
        |> CatalogCodecs.codec.Write
        |> WireWriter.indentedBytes
        |> fun bytes -> Array.append bytes [| byte '\n' |]

    let render () = renderFrom Catalog.document
