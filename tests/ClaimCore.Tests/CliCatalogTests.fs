module ClaimCore.Tests.CliCatalogTests

open System
open System.IO
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Cli

let private oracle () =
    let path =
        Path.Combine(ClaimCore.TestSupport.RepositoryRoot.find (), "tests/fixtures/cli-frames.tsv")

    let rows = File.ReadAllLines path
    Expect.equal rows.Length 30 "All captured invocations remain exercised"

    for row in rows do
        let cells = row.Split('\t', 2)

        let arguments =
            cells[0].Split(' ', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

        let actual =
            Grammar.prepare
                (fun () -> "11111111-2222-4333-8444-555555555555")
                (arguments @ [ "--dry-run" ])
            |> Fixtures.accepted

        Expect.isOk (Frames.admit actual.Bytes) "Prepared request passes the actual endpoint codec"

        Expect.isTrue
            (JsonNode.DeepEquals(JsonNode.Parse cells[1], JsonNode.Parse actual.Bytes))
            cells[0]

[<Tests>]
let tests =
    testCase "typed CLI produces all thirty independently captured frames" oracle
