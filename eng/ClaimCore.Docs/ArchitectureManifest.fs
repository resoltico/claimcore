namespace ClaimCore.Docs

open System
open System.IO
open System.Text.Json

/// One classified component of `architecture.json`, the repository's single architecture contract.
/// The compiled architecture suite enforces this file; documentation renders from it.
[<NoEquality; NoComparison>]
type ArchitectureComponent =
    {
        Name: string
        Tier: string
        Layer: string
        Project: string
        Role: string
        DependsOn: string list
        TestInventory: bool
    }

type private ArchitectureManifestAnchor = class end

[<RequireQualifiedAccess>]
module ArchitectureManifest =
    let fileName = "architecture.json"
    let private maximumBytes = 64 * 1024

    let private text (name: string) (element: JsonElement) =
        match element.TryGetProperty(name) with
        | true, value when value.ValueKind = JsonValueKind.String ->
            match value.GetString() |> Option.ofObj with
            | Some content when not (String.IsNullOrWhiteSpace content) -> Ok content
            | _ -> Error $"The architecture manifest has an empty '{name}'."
        | _ -> Error $"The architecture manifest entry is missing '{name}'."

    let private names (name: string) (element: JsonElement) =
        match element.TryGetProperty(name) with
        | true, value when value.ValueKind = JsonValueKind.Array ->
            let items =
                value.EnumerateArray()
                |> Seq.map (fun item ->
                    if item.ValueKind = JsonValueKind.String then
                        item.GetString() |> Option.ofObj
                    else
                        None)
                |> Seq.toList

            if items |> List.exists Option.isNone then
                Error $"The architecture manifest '{name}' has an invalid entry."
            else
                Ok(items |> List.choose id)
        | _ -> Error $"The architecture manifest entry is missing '{name}'."

    let private failure =
        function
        | Error message -> Some message
        | Ok _ -> None

    let private flag (name: string) (element: JsonElement) =
        match element.TryGetProperty(name) with
        | true, value when value.ValueKind = JsonValueKind.True -> Ok true
        | true, value when value.ValueKind = JsonValueKind.False -> Ok false
        | _ -> Error $"The architecture manifest entry is missing '{name}'."

    let private component' (element: JsonElement) =
        match
            text "name" element,
            text "tier" element,
            text "layer" element,
            text "project" element,
            text "role" element,
            names "dependsOn" element,
            flag "testInventory" element
        with
        | Ok name, Ok tier, Ok layer, Ok project, Ok role, Ok dependsOn, Ok testInventory ->
            Ok
                {
                    Name = name
                    Tier = tier
                    Layer = layer
                    Project = project
                    Role = role
                    DependsOn = dependsOn
                    TestInventory = testInventory
                }
        | _ -> Error "The architecture manifest has an incomplete component."

    let parse (content: string) =
        if content.Length = 0 || content.Length > maximumBytes then
            Error "The architecture manifest is empty or exceeds its bounded size."
        else
            try
                use document = JsonDocument.Parse(content)
                let root = document.RootElement

                match root.TryGetProperty("version") with
                | true, version when
                    version.ValueKind = JsonValueKind.Number && version.GetInt32() = 1
                    ->
                    match root.TryGetProperty("components") with
                    | true, components when components.ValueKind = JsonValueKind.Array ->
                        let parsed = components.EnumerateArray() |> Seq.map component' |> Seq.toList

                        match parsed |> List.tryPick failure with
                        | Some failure -> Error failure
                        | None ->
                            let items = parsed |> List.choose Result.toOption

                            if items.IsEmpty then
                                Error "The architecture manifest classifies no component."
                            elif
                                (items |> List.map _.Name |> Set.ofList |> Set.count)
                                <> items.Length
                            then
                                Error "The architecture manifest classifies a component twice."
                            else
                                Ok items
                    | _ -> Error "The architecture manifest has no component inventory."
                | _ -> Error "The architecture manifest declares an unsupported version."
            with :? JsonException ->
                Error "The architecture manifest is not valid JSON."

    let private embeddedText =
        lazy
            (let assembly = typeof<ArchitectureManifestAnchor>.Assembly

             use stream =
                 assembly.GetManifestResourceStream("ClaimCore.ArchitectureManifest.json")
                 |> Option.ofObj
                 |> Option.defaultWith (fun () ->
                     invalidOp "The embedded architecture manifest is missing.")

             use reader = new StreamReader(stream)
             reader.ReadToEnd())

    /// The manifest this tool was built against. Evidence validation uses it rather than a path,
    /// so a report is always judged against the architecture the tool actually compiled with.
    let current =
        lazy
            (match parse embeddedText.Value with
             | Ok components -> components
             | Error message -> invalidOp message)

    let private readText (root: RepositoryRoot) =
        Repository.registeredPath root fileName
        |> Result.bind (Repository.ensureExistingSafe root)
        |> Result.mapError (fun _ -> "The architecture manifest is missing or unsafe.")
        |> Result.bind (fun path ->
            try
                Ok(File.ReadAllText path)
            with _ ->
                Error "The architecture manifest could not be read.")

    let load (root: RepositoryRoot) = readText root |> Result.bind parse

    /// A stale tool would describe a different architecture than the one on disk, so the
    /// documentation check refuses to report at all until it has been rebuilt.
    let requireCurrent (root: RepositoryRoot) =
        readText root
        |> Result.bind (fun text ->
            if text = embeddedText.Value then
                Ok()
            else
                Error
                    "The architecture manifest changed after this tool was built. Rebuild ClaimCore.Docs.")

    /// Test runners that publish a compiled test inventory, as the manifest classifies them.
    let testInventoryAssemblies components =
        components
        |> List.filter (fun item -> item.TestInventory)
        |> List.map _.Name
        |> Set.ofList

    let inTier tier components =
        components
        |> List.filter (fun item -> item.Tier = tier)
        |> List.map _.Name
        |> Set.ofList
