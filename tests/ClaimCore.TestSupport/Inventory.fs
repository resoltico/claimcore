module ClaimCore.TestSupport.Inventory

open System.IO
open System.Text.Json

// This fixture is authored independently of product declarations and codecs.
let private source =
    use document =
        JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(RepositoryRoot.find (), "tests/fixtures/inventory.json"))
        )

    document.RootElement.Clone()

let rows key =
    source.GetProperty(key: string).EnumerateArray() |> Seq.toList

let tokens key =
    rows key
    |> List.map (fun row ->
        if row.ValueKind = JsonValueKind.String then
            row.GetString() |> nonNull
        else
            row.GetProperty("token").GetString() |> nonNull)

let fields group =
    source.GetProperty("groups").GetProperty(group: string).EnumerateArray()
    |> Seq.toList

let fieldKeys group =
    fields group
    |> List.map (fun field -> field.GetProperty("key").GetString() |> nonNull)

let fieldCount =
    source.GetProperty("groups").EnumerateObject()
    |> Seq.sumBy (fun group -> group.Value.GetArrayLength())
    |> (+) (rows "specialFields" |> List.length)

let cliFrameCount = source.GetProperty("cliFrameCount").GetInt32()

let readInputs =
    source.GetProperty("readInputs").EnumerateObject()
    |> Seq.map (fun entry ->
        entry.Name,
        entry.Value.EnumerateArray()
        |> Seq.map (fun value -> value.GetString() |> nonNull)
        |> Set.ofSeq)
    |> Map.ofSeq
