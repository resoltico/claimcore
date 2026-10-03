namespace ClaimCore.HostSecurity

open System
open System.Security.Cryptography
open System.Text

/// Separate owner-private key for managed-copy custodian and location commitments.
[<Sealed>]
type ManagedCopyCommitmentKey private (material: byte array) =
    let mutable disposed = false

    member _.Commit(label: string, value: string) =
        if disposed then
            invalidOp "Managed-copy commitment key is unavailable."

        if
            (label <> "custodian" && label <> "location")
            || String.IsNullOrWhiteSpace(value)
            || value.Length > 4096
        then
            invalidArg (nameof value) "Managed-copy commitment input is invalid."

        let input = Encoding.UTF8.GetBytes(label + "\000" + value)

        try
            HMACSHA256.HashData(material, input)
        finally
            CryptographicOperations.ZeroMemory(input)

    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed <- true
                CryptographicOperations.ZeroMemory(material)

    static member Load(path: string) =
        match PrivateFileService.readBinary 32 path with
        | Ok bytes when bytes.Length = 32 && bytes |> Array.exists ((<>) 0uy) ->
            new ManagedCopyCommitmentKey(bytes)
        | Ok bytes ->
            CryptographicOperations.ZeroMemory(bytes)
            invalidOp "Private managed-copy commitment key is invalid."
        | Error _ -> invalidOp "Private managed-copy commitment key is unavailable."
