namespace ClaimCore.Docs

open System
open System.IO
open System.Text.Json

[<RequireQualifiedAccess>]
module DocumentationReport =
    let private codeName code =
        match code with
        | DiagnosticCode.Invocation -> "invocation"
        | DiagnosticCode.UnsafePath -> "unsafe-path"
        | DiagnosticCode.InvalidMarkdown -> "invalid-markdown"
        | DiagnosticCode.GeneratedDrift -> "generated-drift"
        | DiagnosticCode.InvalidLink -> "invalid-link"
        | DiagnosticCode.InvalidContract -> "invalid-contract"
        | DiagnosticCode.InvalidReview -> "invalid-review"
        | DiagnosticCode.InvalidManifest -> "invalid-manifest"
        | DiagnosticCode.InvalidEvidence -> "invalid-evidence"
        | DiagnosticCode.ConcurrentEdit -> "concurrent-edit"
        | DiagnosticCode.Unexpected -> "unexpected"

    let private writeSource (writer: Utf8JsonWriter) (source: SourceIdentity) =
        writer.WriteStartObject("source")

        match source.GitRevision with
        | Some revision -> writer.WriteString("gitRevision", revision)
        | None -> writer.WriteNull("gitRevision")

        writer.WriteString("state", source.State)
        writer.WriteString("contentSha256", source.ContentSha256)
        writer.WriteString("locksSha256", source.LocksSha256)
        writer.WriteEndObject()

    let private writeBlock (writer: Utf8JsonWriter) (block: BlockStatus) =
        writer.WriteStartObject()
        writer.WriteString("id", block.Id)
        writer.WriteString("document", block.Document)
        writer.WriteString("expectedSha256", block.ExpectedSha256)
        writer.WriteString("status", block.Status)
        writer.WriteEndObject()

    let private writeError (writer: Utf8JsonWriter) (error: Diagnostic) =
        writer.WriteStartObject()
        writer.WriteString("code", codeName error.Code)

        match error.Path with
        | Some path -> writer.WriteString("path", path)
        | None -> writer.WriteNull("path")

        match error.Line with
        | Some line -> writer.WriteNumber("line", line)
        | None -> writer.WriteNull("line")

        writer.WriteString("message", error.Message)
        writer.WriteEndObject()

    let write
        (root: RepositoryRoot)
        (operation: string)
        (outcome: string)
        (source: SourceIdentity)
        (blocks: BlockStatus list)
        (documents: int)
        (links: int)
        (contracts: string list)
        (errors: Diagnostic list)
        =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
        writer.WriteStartObject()
        writer.WriteNumber("schemaVersion", 1)
        writer.WriteString("operation", operation)
        writer.WriteString("outcome", outcome)
        writeSource writer source
        writer.WriteStartArray("blocks")
        blocks |> List.sortBy _.Id |> List.iter (writeBlock writer)
        writer.WriteEndArray()
        writer.WriteNumber("documentsChecked", documents)
        writer.WriteNumber("linksChecked", links)
        writer.WriteStartArray("contracts")

        contracts
        |> List.sort
        |> List.iter (fun value -> writer.WriteStringValue(value: string))

        writer.WriteEndArray()
        writer.WriteStartArray("errors")

        errors |> List.iter (writeError writer)
        writer.WriteEndArray()
        writer.WriteEndObject()
        writer.Flush()
        let bytes = stream.ToArray() |> Array.append [| byte '\n' |]

        AtomicFile.write root $"artifacts/docs/{operation}.json" bytes
        |> Result.map ignore
