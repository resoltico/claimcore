namespace ClaimCore.Database

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes

module internal DatabaseContractCatalogue =
    let render (schema: JsonNode) =
        let fingerprint =
            schema.ToJsonString()
            |> Encoding.UTF8.GetBytes
            |> SHA256.HashData
            |> Convert.ToHexStringLower

        let catalogue = JsonObject()
        catalogue.Add("contractKind", JsonValue.Create("ADMINISTRATION_DIAGNOSTICS_V1"))
        catalogue.Add("fingerprint", JsonValue.Create(fingerprint))
        catalogue.Add("responseSchema", schema)
        Encoding.UTF8.GetBytes(catalogue.ToJsonString() + "\n")
