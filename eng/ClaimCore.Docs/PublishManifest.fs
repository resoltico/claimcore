namespace ClaimCore.Docs

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
module PublishManifest =
    let private digestPattern = Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)

    let private slash (value: string) =
        value.Replace(Path.DirectorySeparatorChar, '/')

    let private unsafeEntry (path: string) =
        File.GetAttributes(path) &&& FileAttributes.ReparsePoint <> enum 0

    let private files (outputRoot: string) =
        let root = Path.GetFullPath(outputRoot)

        if not (Directory.Exists(root)) then
            Error "The registered output root does not exist."
        elif unsafeEntry root then
            Error "The registered output root is a symbolic link or junction."
        else
            try
                let found = ResizeArray<PublishFile>()

                let rec walk directory =
                    for entry in Directory.EnumerateFileSystemEntries(directory) |> Seq.sort do
                        if unsafeEntry entry then
                            raise (
                                IOException(
                                    "Output trees may not contain symbolic links or junctions."
                                )
                            )
                        elif Directory.Exists(entry) then
                            walk entry
                        else
                            let info = FileInfo(entry)

                            found.Add(
                                {
                                    Path = Path.GetRelativePath(root, entry) |> slash
                                    Length = info.Length
                                    Sha256 = Repository.sha256File entry
                                }
                            )

                walk root
                Ok(found |> Seq.sortBy _.Path |> Seq.toList)
            with error ->
                Error(error.GetType().Name + ": " + error.Message)

    let treeSha256 (files: PublishFile list) =
        files
        |> List.sortBy _.Path
        |> List.map (fun item ->
            item.Path
            + string (char 0)
            + item.Length.ToString(CultureInfo.InvariantCulture)
            + string (char 0)
            + item.Sha256
            + "\n")
        |> String.concat ""
        |> Repository.sha256Text

    let create
        (stageId: string)
        (sourceSha256: string)
        (locksSha256: string)
        (toolchain: ToolchainIdentity)
        (outputRoot: string)
        =
        match files outputRoot with
        | Error message -> Error message
        | Ok entries ->
            Ok
                {
                    SchemaVersion = 1
                    StageId = stageId
                    SourceSha256 = sourceSha256
                    LocksSha256 = locksSha256
                    Toolchain = toolchain
                    Files = entries
                    TreeSha256 = treeSha256 entries
                }

    let private exactProperties (expected: string list) (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            Error "Expected a JSON object."
        else
            let names = element.EnumerateObject() |> Seq.map _.Name |> Seq.toList

            if names.Length <> (names |> Set.ofList |> Set.count) then
                Error "Duplicate JSON properties are not allowed."
            elif Set.ofList names <> Set.ofList expected then
                Error "The JSON object has missing or unknown properties."
            else
                Ok()

    let private text (name: string) (element: JsonElement) =
        let value = element.GetProperty(name)

        match value.ValueKind, value.GetString() |> Option.ofObj with
        | JsonValueKind.String, Some content -> Ok content
        | _ -> Error $"Property '{name}' must be a string."

    let private optionalText (name: string) (element: JsonElement) =
        let value = element.GetProperty(name)

        match value.ValueKind with
        | JsonValueKind.Null -> Ok None
        | JsonValueKind.String ->
            match value.GetString() |> Option.ofObj with
            | Some content -> Ok(Some content)
            | None -> Error $"Property '{name}' must not be null."
        | _ -> Error $"Property '{name}' must be a string or null."

    let private digest (name: string) (element: JsonElement) =
        text name element
        |> Result.bind (fun value ->
            if digestPattern.IsMatch(value) then
                Ok value
            else
                Error $"Property '{name}' is not SHA-256.")

    let private parseToolchain (element: JsonElement) =
        exactProperties [ "dotnetSdk"; "node"; "npm"; "postgresql"; "os"; "architecture" ] element
        |> Result.bind (fun () ->
            match
                text "dotnetSdk" element,
                optionalText "node" element,
                optionalText "npm" element,
                optionalText "postgresql" element,
                text "os" element,
                text "architecture" element
            with
            | Ok dotnet, Ok node, Ok npm, Ok postgres, Ok os, Ok architecture ->
                Ok
                    {
                        DotnetSdk = dotnet
                        Node = node
                        Npm = npm
                        PostgreSql = postgres
                        OperatingSystem = os
                        Architecture = architecture
                    }
            | _ -> Error "Toolchain properties have invalid values.")

    let private validRelativePath (path: string) =
        not (String.IsNullOrWhiteSpace(path))
        && not (Path.IsPathRooted(path))
        && not (path.Contains(char 92))
        && not (path.Contains(char 0))
        && (path.Split('/')
            |> Array.forall (fun part -> part <> "" && part <> "." && part <> ".."))

    let private parseFile (element: JsonElement) =
        exactProperties [ "path"; "length"; "sha256" ] element
        |> Result.bind (fun () ->
            match text "path" element, digest "sha256" element with
            | Ok path, Ok sha when validRelativePath path ->
                let length = element.GetProperty("length")
                let mutable parsed = 0L

                if length.TryGetInt64(&parsed) && parsed >= 0L then
                    Ok
                        {
                            Path = path
                            Length = parsed
                            Sha256 = sha
                        }
                else
                    Error "File length must be a non-negative 64-bit integer."
            | Ok _, Ok _ -> Error "Manifest file path is unsafe."
            | _ -> Error "Manifest file properties have invalid values.")

    let private manifestFiles (root: JsonElement) tree =
        let fileElement = root.GetProperty("files")

        if fileElement.ValueKind <> JsonValueKind.Array then
            Error "Manifest files must be an array."
        else
            let parsed = fileElement.EnumerateArray() |> Seq.map parseFile |> Seq.toList

            match
                parsed
                |> List.tryPick (function
                    | Error value -> Some value
                    | _ -> None)
            with
            | Some error -> Error error
            | None ->
                let entries =
                    parsed
                    |> List.choose (function
                        | Ok value -> Some value
                        | _ -> None)

                let paths = entries |> List.map _.Path

                if
                    paths <> List.sort paths || paths.Length <> (paths |> Set.ofList |> Set.count)
                then
                    Error "Manifest files must be uniquely sorted by ordinal path."
                elif treeSha256 entries <> tree then
                    Error "Manifest tree digest does not match its file records."
                else
                    Ok entries

    let private parseRoot (root: JsonElement) =
        let mutable schema = 0

        if not (root.GetProperty("schemaVersion").TryGetInt32(&schema)) || schema <> 1 then
            Error "Unsupported publish-manifest schema version."
        else
            match
                text "stageId" root,
                digest "sourceSha256" root,
                digest "locksSha256" root,
                parseToolchain (root.GetProperty("toolchain")),
                digest "treeSha256" root
            with
            | Ok stage, Ok source, Ok locks, Ok tools, Ok tree ->
                manifestFiles root tree
                |> Result.map (fun entries ->
                    {
                        SchemaVersion = schema
                        StageId = stage
                        SourceSha256 = source
                        LocksSha256 = locks
                        Toolchain = tools
                        Files = entries
                        TreeSha256 = tree
                    })
            | _ -> Error "Manifest identity properties have invalid values."

    let parse (bytes: byte array) =
        try
            use document =
                JsonDocument.Parse(
                    ReadOnlyMemory<byte>(bytes),
                    JsonDocumentOptions(
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow
                    )
                )

            let root = document.RootElement

            let properties =
                [
                    "schemaVersion"
                    "stageId"
                    "sourceSha256"
                    "locksSha256"
                    "toolchain"
                    "files"
                    "treeSha256"
                ]

            match exactProperties properties root with
            | Error message -> Error message
            | Ok() -> parseRoot root
        with error ->
            Error(error.GetType().Name + ": " + error.Message)

    let verifyTree (outputRoot: string) (manifest: PublishTreeManifest) =
        match files outputRoot with
        | Error message -> Error message
        | Ok actual when actual = manifest.Files && treeSha256 actual = manifest.TreeSha256 -> Ok()
        | Ok _ -> Error "The output tree does not match the publish manifest."
