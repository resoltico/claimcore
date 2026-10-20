namespace ClaimCore.ContractGeneration

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json

/// Identity inputs are precursor documents only. Dependent resources never enter this set.
module ContractIdentity =
    let digest (bytes: byte array) =
        SHA256.HashData bytes |> Convert.ToHexStringLower

    let manifest (artifacts: Artifact list) =
        let names = artifacts |> List.map _.Name

        if List.distinct names |> List.length <> names.Length then
            invalidOp "Duplicate consumer contract identity input."

        let entries =
            artifacts
            |> List.sortWith (fun left right ->
                StringComparer.Ordinal.Compare(left.Name, right.Name))
            |> List.map (fun artifact ->
                "{\"name\":"
                + JsonSerializer.Serialize artifact.Name
                + ",\"sha256\":"
                + JsonSerializer.Serialize(digest artifact.Bytes)
                + "}")

        "{\"format\":1,\"inputs\":[" + String.concat "," entries + "]}\n"
        |> Encoding.UTF8.GetBytes
