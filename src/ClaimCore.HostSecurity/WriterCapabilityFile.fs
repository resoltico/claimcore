namespace ClaimCore.HostSecurity

open System
open System.Security.Cryptography

/// A separately custodied raw capability for one active witness writer generation.
[<Sealed>]
type WriterCapabilityFile private (material: byte array) =
    let mutable disposed = false

    member _.Use<'value>(action: byte array -> 'value) =
        if disposed then
            invalidOp "Writer capability custody is unavailable."

        action material

    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed <- true
                CryptographicOperations.ZeroMemory(material)

    static member Load(path: string) =
        match PrivateFileService.readBinary 32 path with
        | Ok bytes when bytes.Length = 32 && bytes |> Array.exists ((<>) 0uy) ->
            new WriterCapabilityFile(bytes)
        | Ok bytes ->
            CryptographicOperations.ZeroMemory(bytes)
            invalidOp "Private writer capability is invalid."
        | Error _ -> invalidOp "Private writer capability is unavailable."
