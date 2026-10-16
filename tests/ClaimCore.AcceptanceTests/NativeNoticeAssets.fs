module internal ClaimCore.AcceptanceTests.NativeNoticeAssets

open System
open System.IO
open System.Text.Json
open NativeNoticeManifest

type Asset =
    {
        Path: string
        Library: string
        Kind: string
    }

let private unique (value: JsonElement) =
    let names = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList
    require (names.Length = (Set.ofList names).Count)

let private owned root =
    use document =
        JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(root, "config/architecture.json")).AsMemory()
        )

    document.RootElement.GetProperty("product").EnumerateObject()
    |> Seq.map _.Name
    |> Set.ofSeq

let private resource (entry: JsonProperty) =
    let locale = text "locale" entry.Value |> filename
    Path.Combine(locale, (Path.GetFileName entry.Name |> nonNull))

let private assets identity (library: JsonElement) =
    unique library

    let allowed =
        Set.ofList
            [
                "dependencies"
                "runtime"
                "resources"
                "native"
                "compile"
                "runtimeTargets"
            ]

    for property in library.EnumerateObject() do
        require (Set.contains property.Name allowed)

        if property.Name = "runtimeTargets" then
            require (property.Value.EnumerateObject() |> Seq.isEmpty)

    [
        for kind in [ "runtime"; "resources"; "native" ] do
            let mutable entries = Unchecked.defaultof<JsonElement>

            if library.TryGetProperty(kind, &entries) then
                unique entries

                for entry in entries.EnumerateObject() do
                    let path =
                        if kind = "resources" then
                            resource entry
                        else
                            (Path.GetFileName entry.Name |> nonNull)

                    require (not (String.IsNullOrWhiteSpace path))

                    yield
                        {
                            Path = path
                            Library = identity
                            Kind = kind
                        }
    ]

let private ownLibrary inventory kind (identity: string) =
    let name = identity.Split('/')[0]

    let owner =
        match kind with
        | "project" -> name
        | "reference" ->
            require (name.EndsWith(".Reference", StringComparison.Ordinal))
            name.Substring(0, name.Length - ".Reference".Length)
        | _ -> refuse ()

    require (Set.contains owner inventory)
    owner

let private review inventory (manifest: Manifest) kind identity (entries: Asset list) =
    if kind = "package" then
        let package =
            manifest.Packages |> List.tryFind (fun entry -> entry.Identity = identity)

        let approved = package |> Option.defaultWith refuse |> _.Assets |> Set.ofList

        for entry in entries do
            require (
                Set.contains
                    (entry.Kind + ":" + entry.Path.Replace(Path.DirectorySeparatorChar, '/'))
                    approved
            )
    else
        let owner = ownLibrary inventory kind identity

        for entry in entries do
            require (entry.Kind = "runtime" && entry.Path = owner + ".dll")

let graph root directory name (manifest: Manifest) =
    use document =
        JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(directory, name + ".deps.json")).AsMemory()
        )

    let data = document.RootElement
    let selected = data.GetProperty("runtimeTarget") |> text "name"
    require (selected = ".NETCoreApp,Version=v10.0")
    let target = data.GetProperty("targets").GetProperty selected
    let libraries = data.GetProperty "libraries"
    unique target
    unique libraries
    let inventory = owned root
    let found = ResizeArray<Asset>()

    for library in target.EnumerateObject() do
        let entries = assets library.Name library.Value

        if not entries.IsEmpty then
            let kind = libraries.GetProperty(library.Name) |> text "type"

            review inventory manifest kind library.Name entries

            for entry in entries do
                require (File.Exists(Path.Combine(directory, entry.Path)))
                found.Add entry

    let paths = found |> Seq.map _.Path |> Seq.toList
    require (paths.Length = (Set.ofList paths).Count)
    found |> Seq.toList

let host directory name (manifest: Manifest) =
    use document =
        JsonDocument.Parse(
            File.ReadAllText(Path.Combine(directory, "licenses/apphost-provenance.json"))
        )

    let record = document.RootElement
    fields [ "sdk"; "pack"; "version"; "rid" ] record
    let rid = text "rid" record

    require (
        text "sdk" record = manifest.Host.Sdk
        && text "version" record = manifest.Host.Version
    )

    require (text "pack" record = "Microsoft.NETCore.App.Host." + rid)
    require (Set.contains (text "pack" record) (Set.ofList manifest.Host.Packs))
    require (File.Exists(Path.Combine(directory, name)))

    use runtime =
        JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(directory, name + ".runtimeconfig.json")).AsMemory()
        )

    let options = runtime.RootElement.GetProperty "runtimeOptions"
    let mutable included = Unchecked.defaultof<JsonElement>
    require (not (options.TryGetProperty("includedFrameworks", &included)))

    let native =
        if rid.StartsWith("osx-", StringComparison.Ordinal) then
            "libclaimcore_hostsecurity.dylib"
        else
            "libclaimcore_hostsecurity.so"

    require (
        List.contains native manifest.Native
        && File.Exists(Path.Combine(directory, native))
    )

    native
