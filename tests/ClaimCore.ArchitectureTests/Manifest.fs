module internal ClaimCore.ArchitectureTests.Manifest

open System.IO
open System.Text.Json
open System.Xml.Linq

type Contract =
    {
        Projects: Map<string, Set<string>>
        Rules: Map<string, Set<string>>
    }

let private entries (value: JsonElement) =
    value.EnumerateObject()
    |> Seq.map (fun item ->
        item.Name,
        (item.Value.EnumerateArray()
         |> Seq.map (fun value -> value.GetString() |> nonNull)
         |> Set.ofSeq))
    |> Map.ofSeq

let read root =
    use source =
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "config/architecture.json")))

    let value = source.RootElement
    let product = entries (value.GetProperty "product")
    let tooling = entries (value.GetProperty "tooling")

    if
        Set.intersect (product.Keys |> Set.ofSeq) (tooling.Keys |> Set.ofSeq)
        |> Set.isEmpty
        |> not
    then
        invalidOp "A project cannot be both product and tooling."

    {
        Projects = Map.fold (fun found key value -> Map.add key value found) product tooling
        Rules = entries (value.GetProperty "rules")
    }

let projects root =
    [ "src"; "eng"; "tests" ]
    |> Seq.collect (fun folder ->
        Directory.EnumerateFiles(
            Path.Combine(root, folder),
            "*.fsproj",
            SearchOption.AllDirectories
        ))
    |> Seq.map (fun path -> (Path.GetFileNameWithoutExtension path |> nonNull), path)
    |> Map.ofSeq

let references path =
    let source = XDocument.Load(path: string)

    source.Descendants(XName.Get "ProjectReference")
    |> Seq.map (fun item ->
        if
            item.AncestorsAndSelf()
            |> Seq.exists (fun parent -> parent.Attribute(XName.Get "Condition") <> null)
        then
            invalidOp ("Conditional project reference: " + path)

        let includePath =
            match item.Attribute(XName.Get "Include") with
            | null -> invalidOp ("Missing project reference path: " + path)
            | value -> value

        let target =
            Path.GetFullPath(
                Path.Combine(Path.GetDirectoryName(path) |> nonNull, includePath.Value)
            )

        if not (File.Exists target) then
            invalidOp ("Missing referenced project: " + target)

        (Path.GetFileNameWithoutExtension target |> nonNull))
    |> Set.ofSeq
