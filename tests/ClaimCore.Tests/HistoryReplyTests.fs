module ClaimCore.Tests.HistoryReplyTests

open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open global.Json.Schema
open ClaimCore.Contract
open ClaimCore.Contract.Protocol

let private codec = Api.caseHistory.Output
let private bytes (text: string) = Encoding.UTF8.GetBytes text

let private receipt =
    """{"operationId":"10000000-0000-4000-8000-000000000001","position":{"sequence":1,"entryHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"recordedAt":"2026-01-01T00:00:00.000000Z","recordedBy":"10000000-0000-4000-8000-000000000001","scope":{"kind":"CASE","caseReference":"x","revision":1,"act":"OPEN","snapshot":{"disclosure":"WITHHELD"}}}"""

let private summary =
    """{"tag":"SUMMARY","change":{"operationId":"10000000-0000-4000-8000-000000000001","position":{"sequence":1,"entryHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"revision":1,"act":"OPEN","recordedAt":"2026-01-01T00:00:00.000000Z","recordedBy":"10000000-0000-4000-8000-000000000001"}}"""

let private full receipt =
    "{\"tag\":\"FULL\",\"receipt\":" + receipt + "}"

let private page entries =
    "{\"outcome\":{\"tag\":\"SUCCEEDED\",\"data\":{\"entries\":["
    + String.concat "," entries
    + "],\"nextCursor\":null}}}"

let private validator () =
    let document =
        ClaimCore.Contract.OpenApi.document
        |> WireWriter.bytes
        |> Encoding.UTF8.GetString
        |> JsonNode.Parse
        |> nonNull

    let root = JsonObject()
    root["components"] <- (document["components"] |> nonNull).DeepClone()
    root["$ref"] <- System.Text.Json.Nodes.JsonValue.Create "#/components/schemas/HistoryReply"
    JsonSchema.FromText(root.ToJsonString(), BuildOptions(Dialect = Dialect.Draft202012))

let private agrees schema expected text =
    use document = JsonDocument.Parse(text: string)
    let admitted = Codec.decode 100000 codec (bytes text)

    Expect.equal
        (Result.isOk admitted)
        expected
        (sprintf "Typed history admission: %A" (Result.map ignore admitted))

    Expect.equal
        ((schema: JsonSchema).Evaluate(document.RootElement).IsValid)
        expected
        "Independent JSON Schema admission"

    admitted

let private pages () =
    let schema = validator ()

    for entries in [ []; [ summary ]; [ full receipt ] ] do
        let admitted =
            agrees schema true (page entries)
            |> Result.defaultWith (fun _ -> failtest "Valid history")

        Expect.equal
            (Codec.encode codec admitted |> Codec.decode 100000 codec)
            (Ok admitted)
            "History round trip"

    let view =
        ResponseCodecs.liveState.Write CliReplyFixtures.snapshot.View
        |> WireWriter.bytes
        |> Encoding.UTF8.GetString

    for snapshot in
        [
            "{\"disclosure\":\"ERASED\"}"
            "{\"disclosure\":\"DISCLOSED\",\"view\":" + view + "}"
        ] do
        let changed = receipt.Replace("{\"disclosure\":\"WITHHELD\"}", snapshot)
        agrees schema true (page [ full changed ]) |> ignore

let private refused () =
    let schema = validator ()

    let authority =
        "{\"kind\":\"AUTHORITY\",\"targetActorId\":\"10000000-0000-4000-8000-000000000001\",\"act\":\"REGISTER_ACTOR\"}"

    let old =
        ResponseCodecs.caseReceipt.Write CliReplyFixtures.receipt
        |> WireWriter.bytes
        |> Encoding.UTF8.GetString

    let wrongScope =
        receipt.Replace(
            "{\"kind\":\"CASE\",\"caseReference\":\"x\",\"revision\":1,\"act\":\"OPEN\",\"snapshot\":{\"disclosure\":\"WITHHELD\"}}",
            authority
        )

    for entries in [ [ full old ]; [ full wrongScope ]; [ summary; full receipt ] ] do
        agrees schema false (page entries) |> ignore

    let definitions = codec.Definitions

    Expect.isFalse
        (Map.containsKey "Disclosure" definitions)
        "Retired snapshot grammar is unreachable"

    Expect.isTrue
        (Map.containsKey "SnapshotDisclosure" definitions)
        "History shares nested snapshot disclosure"

[<Tests>]
let tests =
    testList
        "registry case history"
        [
            testCase "empty summary and every full disclosure round trip" pages
            testCase "flat receipts authority receipts and mixed history refuse" refused
        ]
