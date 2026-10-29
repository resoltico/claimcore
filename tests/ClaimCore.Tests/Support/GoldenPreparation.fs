module ClaimCore.Tests.GoldenPreparation

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open ClaimCore.Json
open ClaimCore.Cli
open ClaimCore.TestSupport

let private exampleFile filename =
    let path = Path.Combine(RepositoryRoot.find (), "tests/fixtures", filename)
    use document = JsonDocument.Parse(File.ReadAllBytes path)
    let root = JsonNode.Parse(File.ReadAllBytes path) |> nonNull
    let cases = (root["cases"] |> nonNull).AsArray()
    let mutable changed = false

    for index, row in document.RootElement.GetProperty("cases").EnumerateArray() |> Seq.indexed do
        if row.GetProperty("valid").GetBoolean() then
            let value = PositiveExamples.value row

            let original =
                row.GetProperty("value").GetRawText()
                |> Encoding.UTF8.GetBytes
                |> StrictJson.read Int32.MaxValue
                |> Result.defaultWith (string >> invalidOp)

            if CanonicalWriter.bytes value <> CanonicalWriter.bytes original then
                changed <- true
                (cases[index] |> nonNull)["value"] <- JsonNode.Parse(WireWriter.bytes value)

    let bytes =
        if changed then
            Encoding.UTF8.GetBytes(
                root.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + "\n"
            )
        else
            File.ReadAllBytes path

    GoldenFiles.update filename bytes

let private frames () =
    let path = Path.Combine(RepositoryRoot.find (), "tests/fixtures/cli-frames.tsv")

    let rows =
        File.ReadAllLines path
        |> Array.map (fun row ->
            let original = row.Split('\t', 2)

            let arguments =
                original[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                |> Array.toList
                |> CliExamples.complete

            let prepared =
                Grammar.prepare (fun () -> "11111111-2222-4333-8444-555555555555") arguments
                |> Result.defaultWith (string >> invalidOp)

            String.concat " " arguments + "\t" + Encoding.UTF8.GetString prepared.Bytes)

    GoldenFiles.update "cli-frames.tsv" (Encoding.UTF8.GetBytes(String.concat "\n" rows + "\n"))

let prepare () =
    if Environment.GetEnvironmentVariable "CLAIMCORE_UPDATE_GOLDENS" = "1" then
        exampleFile "wire-request-examples.json"
        exampleFile "wire-response-examples.json"
        frames ()
