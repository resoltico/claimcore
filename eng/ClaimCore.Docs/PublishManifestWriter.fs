namespace ClaimCore.Docs

open System.IO
open System.Text.Json

[<RequireQualifiedAccess>]
module PublishManifestWriter =
    let serialize (manifest: PublishTreeManifest) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
        writer.WriteStartObject()
        writer.WriteNumber("schemaVersion", manifest.SchemaVersion)
        writer.WriteString("stageId", manifest.StageId)
        writer.WriteString("sourceSha256", manifest.SourceSha256)
        writer.WriteString("locksSha256", manifest.LocksSha256)
        writer.WriteStartObject("toolchain")
        writer.WriteString("dotnetSdk", manifest.Toolchain.DotnetSdk)

        let optional (name: string) (value: string option) =
            match value with
            | Some text -> writer.WriteString(name, text)
            | None -> writer.WriteNull(name)

        optional "node" manifest.Toolchain.Node
        optional "npm" manifest.Toolchain.Npm
        optional "postgresql" manifest.Toolchain.PostgreSql
        writer.WriteString("os", manifest.Toolchain.OperatingSystem)
        writer.WriteString("architecture", manifest.Toolchain.Architecture)
        writer.WriteEndObject()
        writer.WriteStartArray("files")

        for file in manifest.Files do
            writer.WriteStartObject()
            writer.WriteString("path", file.Path)
            writer.WriteNumber("length", file.Length)
            writer.WriteString("sha256", file.Sha256)
            writer.WriteEndObject()

        writer.WriteEndArray()
        writer.WriteString("treeSha256", manifest.TreeSha256)
        writer.WriteEndObject()
        writer.Flush()
        stream.ToArray() |> Array.append [| byte '\n' |]
