module ClaimCore.ArchitectureTests.ProductPolicy

open System
open System.IO
open System.Text.Json
open ClaimCore.TestSupport

/// One classified repository component. The reviewed maximum direct graph, package set, and
/// internals grants all come from `architecture.json`; nothing here restates them.
[<NoEquality; NoComparison>]
type Component =
    {
        Name: string
        Tier: string
        Layer: string
        Project: string
        Role: string
        DependsOn: string list
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

let manifestPath () =
    Path.Combine(RepositoryRoot.find (), "architecture.json")

let private read () =
    let path = manifestPath ()

    if not (File.Exists path) then
        invalidOp "The architecture manifest is missing."

    use document = JsonDocument.Parse(File.ReadAllText path)
    let root = document.RootElement

    if root.GetProperty("version").GetInt32() <> 1 then
        invalidOp "The architecture manifest declares an unsupported version."

    let parsed =
        root.GetProperty("components").EnumerateArray()
        |> Seq.map (fun element ->
            {
                Name = requireText element "name"
                Tier = requireText element "tier"
                Layer = requireText element "layer"
                Project = requireText element "project"
                Role = requireText element "role"
                DependsOn = requireNames element "dependsOn"
                Packages = requireNames element "packages"
                FrameworkReferences = requireNames element "frameworkReferences"
                InternalsVisibleTo = requireNames element "internalsVisibleTo"
            })
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

    parsed

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
