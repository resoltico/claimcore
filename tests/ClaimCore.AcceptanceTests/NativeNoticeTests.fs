module ClaimCore.AcceptanceTests.NativeNoticeTests

open System
open System.IO
open System.Text.Json.Nodes
open Expecto
open ClaimCore.TestSupport

let private copy directory target =
    Directory.CreateDirectory target |> ignore

    for file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories) do
        let destination = Path.Combine(target, Path.GetRelativePath(directory, file))

        Directory.CreateDirectory(Path.GetDirectoryName destination |> nonNull)
        |> ignore

        File.Copy(file, destination)

let private changedDeps directory change =
    let file = Path.Combine(directory, "ClaimCore.Cli.deps.json")
    let root = JsonNode.Parse(File.ReadAllText file) |> nonNull
    change root
    File.WriteAllText(file, root.ToJsonString())

let private selected (root: JsonNode) =
    let runtime = root["runtimeTarget"] |> nonNull
    let name = runtime["name"] |> nonNull |> _.GetValue<string>()

    root["targets"]
    |> nonNull
    |> fun targets -> targets[name] |> nonNull |> _.AsObject()

let private renamePackage replacement directory =
    changedDeps directory (fun root ->
        let target = selected root
        let libraries = root["libraries"] |> nonNull |> _.AsObject()

        for entries in [ target; libraries ] do
            let original = entries["FSharp.Core/10.1.401"] |> nonNull
            entries.Remove "FSharp.Core/10.1.401" |> ignore
            entries.Add(replacement, original))

let private resource directory =
    Directory.EnumerateFiles(directory, "*.resources.dll", SearchOption.AllDirectories)
    |> Seq.head

let private omitResource directory =
    changedDeps directory (fun root ->
        let library = (selected root)["FSharp.Core/10.1.401"] |> nonNull
        let entries = library["resources"] |> nonNull |> _.AsObject()
        let first = entries |> Seq.head |> _.Key
        entries.Remove first |> ignore)

let private unreviewedAsset kind file directory =
    File.WriteAllBytes(Path.Combine(directory, file), [| 1uy |])

    changedDeps directory (fun root ->
        let library = (selected root)["FSharp.Core/10.1.401"] |> nonNull
        let mutable entries = Unchecked.defaultof<JsonNode | null>

        if not (library.AsObject().TryGetPropertyValue(kind, &entries)) then
            library[kind] <- JsonObject()

        let values = library[kind] |> nonNull |> _.AsObject()
        values.Add(file, JsonObject()))

let private unreviewedHost directory =
    let file = Path.Combine(directory, "licenses/apphost-provenance.json")
    let value = JsonNode.Parse(File.ReadAllText file) |> nonNull
    value["version"] <- JsonValue.Create("99.0.0")
    File.WriteAllText(file, value.ToJsonString())

let private damageCases native =
    [
        "absent text",
        fun directory -> File.Delete(Path.Combine(directory, "licenses/Microsoft-MIT.txt"))
        "empty text",
        fun directory ->
            File.WriteAllText(Path.Combine(directory, "licenses/Microsoft-MIT.txt"), "")
        "unrelated text",
        fun directory ->
            File.WriteAllText(Path.Combine(directory, "licenses/Microsoft-MIT.txt"), "unrelated")
        "unknown package", renamePackage "Unreviewed.Component/1.0.0"
        "unreviewed version", renamePackage "FSharp.Core/99.0.0"
        "unreviewed runtime asset", unreviewedAsset "runtime" "unreviewed.dll"
        "unreviewed native asset", unreviewedAsset "native" "unreviewed.so"
        "unreviewed host version", unreviewedHost
        "missing apphost", fun directory -> File.Delete(Path.Combine(directory, "ClaimCore.Cli"))
        "unexplained runtime",
        fun directory -> File.WriteAllBytes(Path.Combine(directory, "unexplained.so"), [| 1uy |])
        "missing resource", fun directory -> File.Delete(resource directory)
        "omitted resource contribution", omitResource
        "missing native contribution", fun directory -> File.Delete(Path.Combine(directory, native))
        "test supervisor leaked",
        fun directory ->
            File.WriteAllBytes(Path.Combine(directory, "claimcore-test-supervisor"), [| 1uy |])
    ]

let private manifestRefusals directory root =
    let source = Path.Combine(root, "eng/licenses/native-notices.json")
    let original = File.ReadAllText source
    let file = Path.Combine(directory, "invalid-notices.json")

    let missing = JsonNode.Parse(original) |> nonNull
    let texts = missing["texts"] |> nonNull |> _.AsArray()
    texts.RemoveAt 0
    let duplicate = JsonNode.Parse(original) |> nonNull
    let repeated = duplicate["packages"] |> nonNull |> _.AsArray()
    repeated.Add((repeated[0] |> nonNull).DeepClone())

    let cases =
        [
            "{}"
            original.Replace("\"format\": 1", "\"format\": 1, \"format\": 1")
            missing.ToJsonString()
            duplicate.ToJsonString()
        ]

    for content in cases do
        File.WriteAllText(file, content)

        Expect.throws
            (fun () -> NativeNoticeManifest.read file |> ignore)
            "Malformed coverage refuses"

let private crossRidRefusal root directory =
    let rid =
        if OperatingSystem.IsMacOS() then
            "linux-x64"
        else
            "osx-arm64"

    let output = Path.Combine(directory, "opposite-rid")

    let arguments =
        [
            "publish"
            Path.Combine(root, "src/ClaimCore.Cli/ClaimCore.Cli.fsproj")
            "--configuration"
            "Release"
            "--no-build"
            "--no-restore"
            "--runtime"
            rid
            "--output"
            output
        ]

    let start =
        PublishedProcesses.start
            "dotnet"
            arguments
            (System.Collections.Generic.Dictionary<string, string>())

    let result = PublishedProcesses.run 120_000 start None
    Expect.notEqual result.ExitCode 0 "An actual cross-RID publication refuses"

    let message =
        System.Text.Encoding.UTF8.GetString result.StandardOutput
        + System.Text.Encoding.UTF8.GetString result.StandardError

    Expect.stringContains
        message
        "cross-RID builds are unsupported"
        "Native host validation causes refusal"

    Expect.isFalse (Directory.Exists output) "No mislabeled publication directory is created"

let private qualify () =
    let root = RepositoryRoot.find ()

    let directory =
        Directory
            .CreateDirectory(
                Path.Combine(
                    root,
                    "artifacts",
                    "verification",
                    "native-notices-" + Guid.NewGuid().ToString("N")
                )
            )
            .FullName

    try
        let mutable distributed = Set.empty

        for name in [ "ClaimCore.Cli"; "ClaimCore.Database"; "ClaimCore.Web" ] do
            let binary = PublishedProcesses.publish root directory name
            let output = Path.GetDirectoryName binary |> nonNull
            distributed <- Set.union distributed (NativeNotices.qualify root output name)

        let manifest =
            NativeNoticeManifest.read (Path.Combine(root, "eng/licenses/native-notices.json"))

        Expect.equal
            distributed
            (manifest.Packages |> List.map _.Identity |> Set.ofList)
            "No obsolete package review records"

        let original = Path.Combine(directory, "ClaimCore.Cli")
        let native = NativeNoticeAssets.host original "ClaimCore.Cli" manifest

        for index, (reason, damage) in damageCases native |> List.indexed do
            let target = Path.Combine(directory, string index)
            copy original target
            damage target

            Expect.throws
                (fun () -> NativeNotices.qualify root target "ClaimCore.Cli" |> ignore)
                reason

        manifestRefusals directory root
        crossRidRefusal root directory
    finally
        Directory.Delete(directory, true)

[<Tests>]
let tests =
    testCase
        "native notices cover actual publications and refuse incomplete distribution evidence"
        qualify
