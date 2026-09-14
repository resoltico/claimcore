module ClaimCore.ArchitectureTests.InspectionReport

open System
open System.IO
open System.Text.Json
open ArchUnitNET.Domain

type AssemblyCount = { Name: string; InspectedTypes: int }
type Edge = { Source: string; Target: string }

type Report =
    {
        Assemblies: AssemblyCount list
        Edges: Edge list
    }

let private ordinal left right =
    StringComparer.Ordinal.Compare(left, right)

let create (names: string list) (architecture: Architecture) =
    let ordered = names |> List.sortWith ordinal

    if ordered.IsEmpty || ordered.Length <> (Set.ofList ordered).Count then
        invalidOp "Architecture report requires distinct classified product assemblies."

    let selected = Set.ofList ordered
    let types = architecture.Types |> Seq.toArray

    let assemblies =
        ordered
        |> List.map (fun name ->
            let count =
                types |> Array.filter (fun item -> item.Assembly.Name = name) |> Array.length

            if count = 0 then
                invalidOp ("Architecture report omitted required assembly: " + name)

            { Name = name; InspectedTypes = count })

    let edges =
        types
        |> Seq.filter (fun item -> selected.Contains item.Assembly.Name)
        |> Seq.collect (fun item ->
            item.Dependencies
            |> Seq.map (fun dependency ->
                if isNull dependency.Origin || isNull dependency.Target then
                    invalidOp "Architecture report found an incomplete product dependency."

                dependency.Origin.Assembly.Name, dependency.Target.Assembly.Name))
        |> Seq.filter (fun (source, target) ->
            source <> target && selected.Contains source && selected.Contains target)
        |> Set.ofSeq
        |> Set.toList
        |> List.sortWith (fun (leftSource, leftTarget) (rightSource, rightTarget) ->
            let bySource = ordinal leftSource rightSource

            if bySource <> 0 then
                bySource
            else
                ordinal leftTarget rightTarget)
        |> List.map (fun (source, target) -> { Source = source; Target = target })

    if edges.IsEmpty then
        invalidOp "Architecture report found no cross-product dependencies."

    {
        Assemblies = assemblies
        Edges = edges
    }

let encode (report: Report) =
    use stream = new MemoryStream()
    use writer = new Utf8JsonWriter(stream)
    writer.WriteStartObject()
    writer.WriteString("format", "claimcore-architecture-inspection")
    writer.WriteNumber("formatVersion", 1)
    writer.WriteString("configuration", "Debug")
    writer.WriteStartArray("assemblies")

    for assembly in report.Assemblies do
        writer.WriteStartObject()
        writer.WriteString("name", assembly.Name)
        writer.WriteNumber("inspectedTypes", assembly.InspectedTypes)
        writer.WriteEndObject()

    writer.WriteEndArray()
    writer.WriteStartArray("edges")

    for edge in report.Edges do
        writer.WriteStartObject()
        writer.WriteString("source", edge.Source)
        writer.WriteString("target", edge.Target)
        writer.WriteEndObject()

    writer.WriteEndArray()
    writer.WriteEndObject()
    writer.Flush()
    let bytes = stream.ToArray()

    if bytes.Length > 16 * 1024 then
        invalidOp "Architecture inspection report exceeds its 16 KiB limit."

    bytes

let writeRequired (bytes: byte array) =
    let configured = Environment.GetEnvironmentVariable("CLAIMCORE_ARCHITECTURE_REPORT")

    if String.IsNullOrWhiteSpace configured then
        invalidOp "CLAIMCORE_ARCHITECTURE_REPORT must name a new report in an existing directory."

    let path =
        try
            Path.GetFullPath(nonNull configured)
        with _ ->
            invalidOp "Architecture inspection report path is invalid."

    let parent = Path.GetDirectoryName path

    if isNull parent || not (Directory.Exists parent) then
        invalidOp "Architecture inspection report parent does not exist."

    try
        use output =
            new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)

        output.Write(bytes, 0, bytes.Length)
    with _ ->
        invalidOp "Architecture inspection report could not be created as a new file."
