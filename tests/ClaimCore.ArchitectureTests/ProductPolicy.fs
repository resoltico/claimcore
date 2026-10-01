module ClaimCore.ArchitectureTests.ProductPolicy

open System
open System.IO
open System.Text.Json
open ClaimCore.TestSupport

/// One classified repository component. The reviewed maximum direct graph, package set, and
/// internals grants all come from `config/architecture.json`; nothing here restates them.
[<NoEquality; NoComparison>]
type Component =
    {
        Name: string
        Tier: string
        Layer: string
        Project: string
        Role: string
        DependsOn: string list
        CompileOnlyDependsOn: string list
        Packages: string list
        FrameworkReferences: string list
        InternalsVisibleTo: string list
    }

let private tiers = Set.ofList [ "product"; "tooling"; "test" ]

let private requireText (element: JsonElement) name =
    match element.TryGetProperty(name: string) with
    | true, value when value.ValueKind = JsonValueKind.String ->
        let text = value.GetString()

        if String.IsNullOrWhiteSpace text then
            invalidOp ("Architecture manifest has an empty '" + name + "'.")

        nonNull text
    | _ -> invalidOp ("Architecture manifest entry is missing '" + name + "'.")

let private requireNames (element: JsonElement) name =
    match element.TryGetProperty(name: string) with
    | true, value when value.ValueKind = JsonValueKind.Array ->
        let items =
            value.EnumerateArray()
            |> Seq.map (fun item ->
                if item.ValueKind <> JsonValueKind.String then
                    invalidOp ("Architecture manifest '" + name + "' has a non-string entry.")

                let text = item.GetString()

                if String.IsNullOrWhiteSpace text then
                    invalidOp ("Architecture manifest '" + name + "' has an empty entry.")

                nonNull text)
            |> Seq.toList

        if items.Length <> (Set.ofList items).Count then
            invalidOp ("Architecture manifest '" + name + "' repeats an entry.")

        if items <> List.sortWith (fun l r -> StringComparer.Ordinal.Compare(l, r)) items then
            invalidOp ("Architecture manifest '" + name + "' is not in ordinal order.")

        items
    | _ -> invalidOp ("Architecture manifest entry is missing '" + name + "'.")

let private optionalNames (element: JsonElement) name =
    match element.TryGetProperty(name: string) with
    | false, _ -> []
    | true, _ -> requireNames element name

let manifestPath () =
    Path.Combine(RepositoryRoot.find (), "config/architecture.json")

let private exactFields allowed (element: JsonElement) =
    if element.ValueKind <> JsonValueKind.Object then
        invalidOp "Architecture policy must contain objects."

    let names = element.EnumerateObject() |> Seq.map _.Name |> Seq.toList

    if
        names.Length <> (Set.ofList names).Count
        || names |> List.exists (fun name -> not (List.contains name allowed))
    then
        invalidOp "Architecture policy contains duplicate or unknown fields."

let private parseComponent (element: JsonElement) =
    exactFields
        [
            "name"
            "tier"
            "layer"
            "project"
            "role"
            "dependsOn"
            "compileOnlyDependsOn"
            "packages"
            "frameworkReferences"
            "internalsVisibleTo"
        ]
        element

    let value =
        {
            Name = requireText element "name"
            Tier = requireText element "tier"
            Layer = requireText element "layer"
            Project = requireText element "project"
            Role = requireText element "role"
            DependsOn = requireNames element "dependsOn"
            CompileOnlyDependsOn = optionalNames element "compileOnlyDependsOn"
            Packages = requireNames element "packages"
            FrameworkReferences = requireNames element "frameworkReferences"
            InternalsVisibleTo = requireNames element "internalsVisibleTo"
        }

    let parts = value.Project.Split('/')

    let folder =
        match value.Tier with
        | "product" -> "src"
        | "tooling" -> "eng"
        | "test" -> "tests"
        | _ -> ""

    if
        parts.Length < 3
        || parts[0] <> folder
        || parts |> Array.exists (fun part -> part = "" || part = "." || part = "..")
        || value.Project.Contains('\\')
        || value.Project.Contains(':')
    then
        invalidOp "Architecture policy project paths must stay inside their repository tier."

    value

/// Strict policy parsing is independently exercised before project or assembly loading.
let parse text =
    use document = JsonDocument.Parse(text: string)
    let root = document.RootElement
    exactFields [ "version"; "description"; "components" ] root
    requireText root "description" |> ignore

    if root.GetProperty("version").GetInt32() <> 1 then
        invalidOp "The architecture manifest declares an unsupported version."

    let parsed =
        root.GetProperty("components").EnumerateArray()
        |> Seq.map parseComponent
        |> Seq.toList

    if parsed.IsEmpty then
        invalidOp "The architecture manifest classifies no component."

    let declared = parsed |> List.map _.Name

    if declared.Length <> (Set.ofList declared).Count then
        invalidOp "The architecture manifest classifies a component twice."

    let known = Set.ofList declared

    for item in parsed do
        if not (tiers.Contains item.Tier) then
            invalidOp ("Architecture manifest has an unknown tier: " + item.Tier)

        if Path.GetFileNameWithoutExtension item.Project <> item.Name then
            invalidOp ("Architecture manifest project name mismatch: " + item.Name)

        for target in item.DependsOn do
            if target = item.Name then
                invalidOp ("Architecture manifest component depends on itself: " + item.Name)

            if not (known.Contains target) then
                invalidOp (item.Name + " depends on an unclassified component: " + target)

        for target in item.CompileOnlyDependsOn do
            if item.Tier <> "product" || not (List.contains target item.DependsOn) then
                invalidOp (item.Name + " has an undeclared compile-only dependency: " + target)

    parsed

let private read () =
    let path = manifestPath ()

    if not (File.Exists path) then
        invalidOp "The architecture manifest is missing."

    parse (File.ReadAllText path)

let private manifest = lazy (read ())

let components = manifest.Value

let inTier tier =
    components |> List.filter (fun item -> item.Tier = tier)

let product = inTier "product"

/// Product assembly identities, in manifest order, for compiled inspection.
let names = product |> List.map _.Name

/// The component name for a historical `part` label such as "Domain".
let name part = "ClaimCore." + part

let parts = names |> List.map (fun item -> item.Substring("ClaimCore.".Length))

let find componentName =
    components
    |> List.tryFind (fun item -> item.Name = componentName)
    |> Option.defaultWith (fun () -> invalidOp ("Unclassified component: " + componentName))

/// Reviewed maximum direct project edges for every classified component.
let allowed =
    components |> List.map (fun item -> item.Name, item.DependsOn) |> Map.ofList

/// Reviewed direct project edges restricted to the product tier.
let productAllowed =
    product |> List.map (fun item -> item.Name, item.DependsOn) |> Map.ofList
