namespace ClaimCore.Contract

open System
open System.Security.Cryptography

module ContractIdentity =
    let private identity =
        lazy
            (OpenApi.document
             |> CanonicalWriter.bytes
             |> SHA256.HashData
             |> Convert.ToHexStringLower)

    let value = identity.Value
