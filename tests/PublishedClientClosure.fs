module internal ClaimCore.TestSupport.PublishedClientClosure

open System.IO
open System.Text.Json

let private forbidden =
    set
        [
            "ClaimCore.Domain"
            "ClaimCore.RecordFormat"
            "ClaimCore.Application"
            "ClaimCore.Postgres"
        ]

/// Inspect actual deliverable files and resolved dependency identities, including copied assemblies.
let violations directory =
    let files =
        Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories)
        |> Seq.map (Path.GetFileNameWithoutExtension >> nonNull)
        |> Set.ofSeq

    use dependencies =
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "claimcore.deps.json")))

    let libraries =
        dependencies.RootElement.GetProperty("libraries").EnumerateObject()
        |> Seq.map (fun library -> library.Name.Split('/')[0])
        |> Set.ofSeq

    Set.intersect forbidden (Set.union files libraries) |> Set.toList
