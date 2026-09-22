namespace ClaimCore.Docs

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Json

[<RequireQualifiedAccess>]
module AtomicFile =
    let private ensureDirectory (root: RepositoryRoot) (path: string) =
        let relative = Path.GetRelativePath(root.Path, Path.GetFullPath(path))
        let parts = relative.Split(Path.DirectorySeparatorChar)
        let mutable current = root.Path

        for part in parts do
            if part <> "" then
                current <- Path.Combine(current, part)

                if Directory.Exists(current) then
                    if File.GetAttributes(current) &&& FileAttributes.ReparsePoint <> enum 0 then
                        raise (
                            IOException(
                                "Manifest directories may not be symbolic links or junctions."
                            )
                        )
                else
                    Directory.CreateDirectory(current) |> ignore

    let private writeFile overwrite (root: RepositoryRoot) (relative: string) (bytes: byte array) =
        match Repository.registeredPath root relative with
        | Error message -> Error message
        | Ok _ when not (relative.StartsWith("artifacts/", StringComparison.Ordinal)) ->
            Error "Generated reports may only be written below artifacts/."
        | Ok target ->
            let directory =
                Path.GetDirectoryName(target) |> Option.ofObj |> Option.defaultValue root.Path

            let temporary = target + ".tmp-" + Guid.NewGuid().ToString("N")

            try
                ensureDirectory root directory

                use stream =
                    new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)

                stream.Write(bytes, 0, bytes.Length)
                stream.Flush(true)
                stream.Close()
                File.Move(temporary, target, overwrite)
                Ok target
            with error ->
                if File.Exists(temporary) then
                    File.Delete(temporary)

                Error(error.GetType().Name + ": " + error.Message)

    let write root relative bytes = writeFile true root relative bytes

    /// Exclusive atomic publication; an existing report is never replaced.
    let writeNew root relative bytes = writeFile false root relative bytes

[<RequireQualifiedAccess>]
module StageManifestFormat =
    let serialize (manifest: StageManifest) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
        writer.WriteStartObject()
        writer.WriteNumber("schemaVersion", manifest.SchemaVersion)
        writer.WriteString("stageId", manifest.StageId)
        writer.WriteString("runId", manifest.RunId)
        writer.WriteNumber("attempt", manifest.Attempt)
        writer.WriteString("outcome", manifest.Outcome)

        writer.WriteString(
            "startedUtc",
            manifest.StartedUtc.ToString("O", CultureInfo.InvariantCulture)
        )

        writer.WriteString(
            "finishedUtc",
            manifest.FinishedUtc.ToString("O", CultureInfo.InvariantCulture)
        )

        writer.WriteString("platform", manifest.Platform)
        writer.WriteStartArray("procedure")

        manifest.Procedure
        |> List.iter (fun value -> writer.WriteStringValue(value: string))

        writer.WriteEndArray()
        writer.WriteStartArray("requiredOutputs")

        manifest.RequiredOutputs
        |> List.iter (fun value -> writer.WriteStringValue(value: string))

        writer.WriteEndArray()
        writer.WriteString("outputRoot", manifest.OutputRoot)
        writer.WritePropertyName("output")

        use output =
            JsonDocument.Parse(
                ReadOnlyMemory<byte>(PublishManifestWriter.serialize manifest.Output)
            )

        output.RootElement.WriteTo(writer)
        writer.WriteEndObject()
        writer.Flush()
        stream.ToArray() |> Array.append [| byte '\n' |]

    let private exact (expected: string list) (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            Error "Expected a JSON object."
        else
            let names = element.EnumerateObject() |> Seq.map _.Name |> Seq.toList

            if names.Length <> (names |> Set.ofList |> Set.count) then
                Error "Duplicate stage-manifest properties are forbidden."
            elif Set.ofList names <> Set.ofList expected then
                Error "Stage manifest has missing or unknown properties."
            else
                Ok()

    let private text (name: string) (element: JsonElement) =
        let value = element.GetProperty(name)

        match value.ValueKind, value.GetString() |> Option.ofObj with
        | JsonValueKind.String, Some content -> Ok content
        | _ -> Error $"Stage property '{name}' must be a string."

    let private textList (name: string) (element: JsonElement) =
        let value = element.GetProperty(name)

        if value.ValueKind <> JsonValueKind.Array then
            Error $"Stage property '{name}' must be an array."
        else
            let values = value.EnumerateArray() |> Seq.toList

            if
                values
                |> List.exists (fun item ->
                    item.ValueKind <> JsonValueKind.String
                    || (item.GetString() |> Option.ofObj).IsNone)
            then
                Error $"Stage property '{name}' must contain strings."
            else
                Ok(values |> List.choose (fun item -> item.GetString() |> Option.ofObj))

    let private timestamp raw =
        let mutable value = DateTimeOffset.MinValue

        if
            DateTimeOffset.TryParseExact(
                raw,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                &value
            )
        then
            Ok value
        else
            Error "Stage timestamps must use round-trip ISO-8601."

    let private parseRoot (root: JsonElement) =
        let mutable schema = 0
        let mutable attempt = 0

        if not (root.GetProperty("schemaVersion").TryGetInt32(&schema)) || schema <> 2 then
            Error "Unsupported stage-manifest schema."
        elif not (root.GetProperty("attempt").TryGetInt32(&attempt)) || attempt < 1 then
            Error "Stage attempt must be a positive integer."
        else
            match
                text "stageId" root,
                text "runId" root,
                text "outcome" root,
                text "startedUtc" root |> Result.bind timestamp,
                text "finishedUtc" root |> Result.bind timestamp,
                text "platform" root,
                textList "procedure" root,
                textList "requiredOutputs" root,
                text "outputRoot" root,
                root.GetProperty("output").GetRawText()
                |> Encoding.UTF8.GetBytes
                |> PublishManifest.parse
            with
            | Ok stage,
              Ok run,
              Ok outcome,
              Ok started,
              Ok finished,
              Ok platform,
              Ok procedure,
              Ok required,
              Ok outputRoot,
              Ok output ->
                Ok
                    {
                        SchemaVersion = schema
                        StageId = stage
                        RunId = run
                        Attempt = attempt
                        Outcome = outcome
                        StartedUtc = started
                        FinishedUtc = finished
                        Platform = platform
                        Procedure = procedure
                        RequiredOutputs = required
                        OutputRoot = outputRoot
                        Output = output
                    }
            | _ -> Error "Stage manifest contains invalid values."

    let parse (bytes: byte array) =
        try
            use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
            let root = document.RootElement

            let properties =
                [
                    "schemaVersion"
                    "stageId"
                    "runId"
                    "attempt"
                    "outcome"
                    "startedUtc"
                    "finishedUtc"
                    "platform"
                    "procedure"
                    "requiredOutputs"
                    "outputRoot"
                    "output"
                ]

            match exact properties root with
            | Error message -> Error message
            | Ok() -> parseRoot root
        with error ->
            Error(error.GetType().Name + ": " + error.Message)

    let validate (definition: StageDefinition) (runId: string) attempt (manifest: StageManifest) =
        if manifest.SchemaVersion <> 2 then
            Error "Stage schema does not match its compiled registration."
        elif manifest.StageId <> definition.Id || manifest.Output.StageId <> definition.Id then
            Error "Stage identity does not match its compiled registration."
        elif manifest.RunId <> runId || manifest.Attempt <> attempt then
            Error "Stage run identity does not match the requested evidence run."
        elif not (definition.AllowedPlatforms |> List.contains manifest.Platform) then
            Error "Stage platform does not match its compiled registration."
        elif manifest.Procedure <> definition.Procedure then
            Error "Stage procedure does not match its compiled registration."
        elif
            manifest.RequiredOutputs
            <> (definition.RequiredOutputs |> List.map Stages.displayRequirement)
        then
            Error "Stage output requirements do not match their compiled registration."
        elif manifest.FinishedUtc < manifest.StartedUtc then
            Error "Stage finish precedes its start."
        elif manifest.Outcome <> "success" then
            Error $"Required stage '{manifest.StageId}' did not succeed."
        elif
            definition.RequiredOutputs
            |> List.exists (Stages.satisfies manifest.Platform manifest.Output.Files >> not)
        then
            Error "A required stage output is missing."
        else
            Ok()
