namespace ClaimCore.Contracts

open System
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.RecordFormat

/// A canonical array frames named JSON inputs; catalogue self-identity and derived outputs are absent.
module ContractIdentity =
    let preimage inputs =
        let names = inputs |> List.map fst

        if names.Length <> (Set.ofList names).Count then
            invalidArg "inputs" "Contract identity input names must be unique."

        let values =
            inputs
            |> List.map (fun (name, bytes: byte array) ->
                use document = JsonDocument.Parse(ReadOnlyMemory bytes)

                CanonicalValue.Object
                    [
                        "name", CanonicalValue.String name
                        "value",
                        ClaimCore.RecordFormat.CanonicalJson.ofElement document.RootElement
                    ])

        CanonicalValue.Object
            [
                "purpose", CanonicalValue.String "claimcore/wire-contract"
                "inputs", CanonicalValue.Array values
            ]
        |> ClaimCore.RecordFormat.CanonicalJson.encode

    let fingerprint inputs =
        preimage inputs |> SHA256.HashData |> Convert.ToHexStringLower
