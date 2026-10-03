namespace ClaimCore.Witness

open System
open System.Security.Cryptography
open System.Text.Json

/// Parses one closed owner-private witness key-ring format for runtime and administration.
module KeyRingCodec =
    let private exact names (element: JsonElement) =
        element.ValueKind = JsonValueKind.Object
        && (element.EnumerateObject() |> Seq.length) = List.length names
        && (element.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq) = Set.ofList names

    let parse (bytes: byte array) : IKeyCustody =
        use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
        let root = document.RootElement

        if
            not (exact [ "version"; "activeKeyId"; "keys" ] root)
            || root.GetProperty("version").GetInt32() <> 1
        then
            invalidOp "Private witness key ring is invalid."

        let active = root.GetProperty("activeKeyId").GetGuid()
        let entries = root.GetProperty("keys")

        if
            entries.ValueKind <> JsonValueKind.Array
            || entries.GetArrayLength() < 1
            || entries.GetArrayLength() > 64
        then
            invalidOp "Private witness key ring is invalid."

        let decoded = ResizeArray<Guid * byte array>()

        try
            for entry in entries.EnumerateArray() do
                if not (exact [ "id"; "materialBase64" ] entry) then
                    invalidOp "Private witness key ring is invalid."

                let keyId = entry.GetProperty("id").GetGuid()

                let encoded =
                    entry.GetProperty("materialBase64").GetString()
                    |> Option.ofObj
                    |> Option.defaultWith (fun () ->
                        invalidOp "Private witness key ring is invalid.")

                let material = Convert.FromBase64String(encoded)

                if material.Length <> 32 || Convert.ToBase64String(material) <> encoded then
                    CryptographicOperations.ZeroMemory(material)
                    invalidOp "Private witness key ring is invalid."

                decoded.Add(keyId, material)

            new KeyRing(active, decoded) :> IKeyCustody
        finally
            for _, material in decoded do
                CryptographicOperations.ZeroMemory(material)
