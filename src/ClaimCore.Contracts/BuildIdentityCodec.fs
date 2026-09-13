namespace ClaimCore.Contracts

open System.Buffers
open System.Text.Json
open ClaimCore.Application

/// Canonical configuration-free product identity shared by executable discovery surfaces.
[<RequireQualifiedAccess>]
module BuildIdentityCodec =
    let bytes (identity: BuildIdentity) =
        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = false))
        writer.WriteStartObject()
        writer.WriteString("application", identity.Product)
        writer.WriteString("version", identity.Version)
        writer.WriteString("assemblyVersion", identity.AssemblyVersion)
        writer.WriteString("fileVersion", identity.FileVersion)
        writer.WriteEndObject()
        writer.Flush()
        Array.append (buffer.WrittenSpan.ToArray()) [| byte '\n' |]
