module ClaimCore.AcceptanceTests.PublishManifest

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

let private digest (bytes: byte array) =
    SHA256.HashData(bytes) |> Convert.ToHexStringLower

let private requiredString (name: string) (element: JsonElement) =
    element.GetProperty(name).GetString()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> invalidOp ($"Publish manifest property {name} is null."))

let private requireDigest name (value: string) =
    if
        value.Length <> 64
        || value |> Seq.exists (fun c -> not (Char.IsAsciiHexDigitLower(c)))
    then
        invalidOp ($"Publish manifest has an invalid {name} digest.")

let private safeRelativePath (value: string) =
    if
        String.IsNullOrWhiteSpace(value)
        || value.Contains('\\')
        || value.Contains('\u0000')
        || Path.IsPathFullyQualified(value)
        || value.Split('/')
           |> Array.exists (fun segment -> segment = "" || segment = "." || segment = "..")
    then
        invalidOp "Publish manifest contains an unsafe relative path."

    value

let private regularFiles (root: string) =
    Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
    |> Seq.map (fun path ->
        let info = FileInfo(path)
        let attributes = info.Attributes

        if attributes.HasFlag(FileAttributes.ReparsePoint) then
            invalidOp "Published artifact contains a reparse point."

        let relative =
            Path.GetRelativePath(root, path).Replace('\\', '/') |> safeRelativePath

        relative, info.Length, digest (File.ReadAllBytes(path)))
    |> Seq.sortBy (fun (path, _, _) -> path)
    |> Seq.toList

let private treeDigest (files: (string * int64 * string) list) =
    let builder = StringBuilder()

    for path, length, sha256 in files do
        builder
            .Append(path)
            .Append('\u0000')
            .Append(length.ToString(CultureInfo.InvariantCulture))
            .Append('\u0000')
            .Append(sha256)
            .Append('\n')
        |> ignore

    builder.ToString() |> Encoding.UTF8.GetBytes |> digest

let private exactProperties (expected: Set<string>) (element: JsonElement) =
    let actual = element.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

    if actual <> expected then
        invalidOp "Publish manifest has an unexpected object shape."

let verify (expectedStage: string) (root: string) (manifestPath: string) =
    use document = JsonDocument.Parse(File.ReadAllBytes(manifestPath))
    let manifest = document.RootElement

    exactProperties
        (Set.ofList
            [
                "schemaVersion"
                "stageId"
                "sourceSha256"
                "locksSha256"
                "toolchain"
                "files"
                "treeSha256"
            ])
        manifest

    if manifest.GetProperty("schemaVersion").GetInt32() <> 1 then
        invalidOp "Publish manifest schema version is unsupported."

    if requiredString "stageId" manifest <> expectedStage then
        invalidOp "Publish manifest stage identity does not match."

    requireDigest "source" (requiredString "sourceSha256" manifest)
    requireDigest "locks" (requiredString "locksSha256" manifest)

    let declared =
        manifest.GetProperty("files").EnumerateArray()
        |> Seq.map (fun item ->
            exactProperties (Set.ofList [ "path"; "length"; "sha256" ]) item
            let path = requiredString "path" item |> safeRelativePath
            let length = item.GetProperty("length").GetInt64()
            let sha256 = requiredString "sha256" item
            requireDigest "file" sha256
            path, length, sha256)
        |> Seq.toList

    if declared <> (declared |> List.sortBy (fun (path, _, _) -> path)) then
        invalidOp "Publish manifest files are not in canonical order."

    if
        declared |> Seq.map (fun (path, _, _) -> path) |> Seq.distinct |> Seq.length
        <> declared.Length
    then
        invalidOp "Publish manifest contains duplicate paths."

    let actual = regularFiles root

    if declared <> actual then
        invalidOp "Published artifact tree does not match its manifest."

    let expectedTree = requiredString "treeSha256" manifest
    requireDigest "tree" expectedTree

    if treeDigest actual <> expectedTree then
        invalidOp "Published artifact tree digest does not match."

    expectedTree
