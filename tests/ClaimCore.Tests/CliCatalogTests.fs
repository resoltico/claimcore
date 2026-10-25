module ClaimCore.Tests.CliCatalogTests

open System
open System.IO
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Cli
open ClaimCore.Contract

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

let private byteLimits () =
    let operationId = "11111111-2222-4333-8444-555555555555"

    let prepare reason =
        let args =
            [
                "case"
                "void"
                "REF"
                "--operation-id"
                operationId
                "--expected-revision"
                "1"
                "--reason"
                reason
                "--dry-run"
            ]

        Grammar.prepare (fun () -> operationId) args

    let emptyInput =
        """{"scope":"CASE","operationId":"11111111-2222-4333-8444-555555555555","caseReference":"REF","expectedRevision":1,"act":{"kind":"VOID","reason":""}}"""

    let remaining =
        Limits.MaximumRequestBytes - System.Text.Encoding.UTF8.GetByteCount emptyInput

    let exact = prepare (String('x', remaining)) |> Fixtures.accepted
    Expect.isOk (Frames.admit exact.Bytes) "A request at the byte limit remains preparable"

    for reason in [ String('x', remaining + 1); String.replicate 20_000 "😀" ] do
        match prepare reason with
        | Error [ UsageError.RequestTooLarge maximum ] ->
            Expect.equal maximum Limits.MaximumRequestBytes "The frame reader's bound is reported"

            for language in LocalizationPolicy.languages do
                let text =
                    UsageText.render
                        language
                        [ "case"; "void" ]
                        [ UsageError.RequestTooLarge maximum ]

                Expect.stringContains text (string maximum) "Localized limit guidance"
        | Error issues -> failtestf "Wrong size refusal: %A" issues
        | Ok _ -> failtest "Dry-run must not emit a frame the frame reader rejects"

[<Tests>]
let tests =
    testList
        "CLI catalog behavior"
        [
            testCase "typed CLI produces all thirty independently captured frames" oracle
            testCase "prepared frames enforce exact and multibyte request size bounds" byteLimits
        ]
