module internal ClaimCore.AcceptanceTests.NativeNoticeManifest

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json

let refuse () =
    invalidOp "Native publication notice coverage refused."

let require condition =
    if not condition then
        refuse ()

let fields expected (value: JsonElement) =
    require (value.ValueKind = JsonValueKind.Object)
    let names = value.EnumerateObject() |> Seq.map _.Name |> Seq.toList

    require (
        names.Length = (Set.ofList names).Count
        && Set.ofList names = Set.ofList expected
    )

let text (name: string) (value: JsonElement) =
    let field = value.GetProperty name
    require (field.ValueKind = JsonValueKind.String)
    let result = field.GetString() |> nonNull
    require (not (String.IsNullOrWhiteSpace result))
    result

let strings (name: string) (value: JsonElement) =
    let field = value.GetProperty name
    require (field.ValueKind = JsonValueKind.Array)

    let values =
        field.EnumerateArray()
        |> Seq.map (fun item -> item.GetString() |> nonNull)
        |> Seq.toList

    require (not values.IsEmpty && values.Length = (Set.ofList values).Count)
    values

let filename (name: string) =
    require (
        name <> ""
        && name <> "."
        && name <> ".."
        && name = (Path.GetFileName name |> nonNull)
        && not (name.Contains '\\')
    )

    name

type Text = { File: string; Hash: string }

type Package =
    {
        Identity: string
        Texts: string list
        Assets: string list
    }

type Host =
    {
        Sdk: string
        Version: string
        Packs: string list
        Texts: string list
    }

type Manifest =
    {
        Texts: Text list
        Packages: Package list
        Host: Host
        Native: string list
    }

let private notice (value: JsonElement) =
    fields [ "file"; "sha256"; "source" ] value
    text "source" value |> ignore

    {
        File = text "file" value |> filename
        Hash = text "sha256" value
    }

let private package (value: JsonElement) =
    fields
        [
            "id"
            "version"
            "repository"
            "packageRevision"
            "licenseRevision"
            "texts"
            "assets"
        ]
        value

    for field in [ "repository"; "packageRevision"; "licenseRevision" ] do
        text field value |> ignore

    {
        Identity = text "id" value + "/" + text "version" value
        Texts = strings "texts" value |> List.map filename
        Assets = strings "assets" value
    }

let private host (value: JsonElement) =
    fields [ "sdk"; "version"; "revision"; "packs"; "texts" ] value
    text "revision" value |> ignore

    {
        Sdk = text "sdk" value
        Version = text "version" value
        Packs = strings "packs" value
        Texts = strings "texts" value |> List.map filename
    }

let read path =
    use document = JsonDocument.Parse(File.ReadAllBytes(path).AsMemory())
    let root = document.RootElement
    fields [ "format"; "scope"; "texts"; "packages"; "host"; "native" ] root
    require (root.GetProperty("format").GetInt32() = 1)
    text "scope" root |> ignore

    let texts =
        root.GetProperty("texts").EnumerateArray() |> Seq.map notice |> Seq.toList

    let packages =
        root.GetProperty("packages").EnumerateArray() |> Seq.map package |> Seq.toList

    let native = root.GetProperty "native"
    fields [ "project"; "assets"; "license" ] native

    require (
        text "project" native = "ClaimCore.HostSecurity"
        && text "license" native = "LICENSE"
    )

    require (
        not texts.IsEmpty
        && texts.Length = (texts |> List.map _.File |> Set.ofList).Count
    )

    require (
        not packages.IsEmpty
        && packages.Length = (packages |> List.map _.Identity |> Set.ofList).Count
    )

    let host = host (root.GetProperty "host")
    let known = texts |> List.map _.File |> Set.ofList

    for names in host.Texts :: (packages |> List.map _.Texts) do
        require (names |> List.forall (fun name -> Set.contains name known))

    {
        Texts = texts
        Packages = packages
        Host = host
        Native = strings "assets" native |> List.map filename
    }

let exactFile expected actual =
    let bytes = File.ReadAllBytes expected
    require (bytes.Length > 0 && bytes = File.ReadAllBytes actual)

let delivered root directory =
    let owner = Path.Combine(root, "eng/licenses")
    let output = Path.Combine(directory, "licenses")
    let manifest = read (Path.Combine(owner, "native-notices.json"))

    exactFile
        (Path.Combine(owner, "native-notices.json"))
        (Path.Combine(output, "native-notices.json"))

    for entry in manifest.Texts do
        let source = Path.Combine(owner, entry.File)
        require (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes source)) = entry.Hash)
        exactFile source (Path.Combine(output, entry.File))

    exactFile (Path.Combine(root, "LICENSE")) (Path.Combine(directory, "LICENSE"))
    manifest
