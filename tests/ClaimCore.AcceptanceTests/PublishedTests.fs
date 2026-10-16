module ClaimCore.AcceptanceTests.PublishedTests

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open CliCalls

let private walkthrough (fixture: PublishedFixture.Fixture) =
    let files =
        [
            "01-open.json"
            "02-decide.json"
            "03-record-payment.json"
            "04-close.json"
            "05-reopen.json"
            "06-correct-payment-record.json"
        ]

    let mutable position = Unchecked.defaultof<JsonElement>

    for index, file in List.indexed files do
        let bytes = File.ReadAllBytes(Path.Combine(fixture.Root, "examples", file))
        let receipt = run fixture fixture.Environment bytes 0 |> require "ACCEPTED"

        Expect.equal
            (receipt.GetProperty("revision").GetInt64())
            (int64 (index + 1))
            "Example revision"

        let replay = run fixture fixture.Environment bytes 0 |> require "ALREADY_ACCEPTED"
        Expect.equal (replay.GetRawText()) (receipt.GetRawText()) "Exact replay receipt"
        position <- receipt.GetProperty("position")

    let current =
        automation fixture "case.get" {| caseReference = "DEMO-0001" |} 0
        |> require "SUCCEEDED"

    let view = current
    Expect.equal (view.GetProperty("revision").GetInt64()) 6L "Six commands"

    Expect.equal
        (view.GetProperty("state").GetProperty("fields").GetProperty("status").GetString())
        "OPENED"
        "Reopened"

    Expect.equal
        (view.GetProperty("state").GetProperty("fields").GetProperty("paymentDate").ValueKind)
        JsonValueKind.Null
        "Clear payment date"

    position

let private erasedHistory fixture =
    let history =
        automation
            fixture
            "case.history"
            {|
                caseReference = "DEMO-0001"
                detail = "FULL"
                limit = 50
            |}
            0
        |> require "SUCCEEDED"

    let entries = history.GetProperty("entries")
    Expect.equal (entries.GetArrayLength()) 9 "All nine accepted acts retain metadata"

    for entry in entries.EnumerateArray() do
        Expect.equal
            (entry.GetProperty("tag").GetString())
            "FULL"
            "Requested receipt detail remains explicit"

        let receipt = entry.GetProperty("receipt")
        let snapshot = receipt.GetProperty("snapshot")
        Expect.equal (snapshot.GetProperty("tag").GetString()) "ERASED" "Business payload is erased"

        Expect.equal
            (snapshot.EnumerateObject() |> Seq.length)
            1
            "No historical payload retained in receipt"

        let names = receipt.EnumerateObject() |> Seq.map _.Name |> Seq.sort |> Seq.toList

        Expect.equal
            names
            [
                "act"
                "caseReference"
                "operationId"
                "position"
                "recordedAt"
                "recordedBy"
                "revision"
                "scope"
                "snapshot"
            ]
            "Receipt retains identity and ledger metadata"

let private stewardship (fixture: PublishedFixture.Fixture) =
    let source =
        Path.Combine(fixture.Root, "examples", "07-void.json") |> File.ReadAllBytes

    let blocked =
        run fixture (interactiveEnvironment fixture "synthetic-editor" "valid") source 2

    Expect.equal (tag blocked) "REJECTED" "Editor cannot steward"

    run fixture (interactiveEnvironment fixture "synthetic-steward" "valid") source 0
    |> require "ACCEPTED"
    |> ignore

    for file in [ "08-reinstate.json"; "09-erase.json" ] do
        let bytes = File.ReadAllBytes(Path.Combine(fixture.Root, "examples", file))

        run fixture (interactiveEnvironment fixture "synthetic-steward" "valid") bytes 0
        |> require "ACCEPTED"
        |> ignore

    let erased =
        automation fixture "case.get" {| caseReference = "DEMO-0001" |} 0
        |> require "SUCCEEDED"

    let tombstone = erased

    Expect.equal
        (tombstone.GetProperty("state").GetProperty("tag").GetString())
        "ERASED"
        "Erased state"

    Expect.equal
        (tombstone.EnumerateObject() |> Seq.length)
        5
        "Erased reference, revision, state and current advice"

    Expect.equal
        (tombstone.GetProperty("offers").GetArrayLength())
        1
        "Only authorized retained history is offered"

    erasedHistory fixture

let private verify (fixture: PublishedFixture.Fixture) (position: JsonElement) =
    let expected =
        (position
            .GetProperty("sequence")
            .GetInt64()
            .ToString(Globalization.CultureInfo.InvariantCulture))
        + ":"
        + (position.GetProperty("entryHash").GetString() |> nonNull)

    let result = fixture.Administration [ "verify"; "--expect"; expected ]
    Expect.equal result.ExitCode 0 "Published owner verify receipt position"

    let rejected =
        fixture.Administration
            [
                "verify"
                "--expect"
                "999999:0000000000000000000000000000000000000000000000000000000000000000"
            ]

    Expect.equal rejected.ExitCode 5 "Wrong checkpoint is verification divergence"

let private inputBoundaries fixture =
    let malformed =
        Encoding.UTF8.GetBytes "{\"endpoint\":\"case.list\",\"input\":{\"limit\":1},\"extra\":true}"

    let reply = run fixture fixture.Environment malformed 2

    Expect.equal
        (reply.GetProperty("kind").GetString())
        "protocolFailure"
        "Strict frame rejects extra property"

    let reader = human fixture "synthetic-reader" "case.list" {| limit = 50 |} 0
    require "SUCCEEDED" reader |> ignore

    let authored =
        File.ReadAllBytes(Path.Combine(fixture.Root, "examples", "01-open.json"))

    let refused =
        run fixture (interactiveEnvironment fixture "synthetic-reader" "valid") authored 2

    Expect.equal (tag refused) "REJECTED" "Reader cannot execute"

    let wrongState =
        run
            fixture
            (interactiveEnvironment fixture "synthetic-owner" "wrong-state")
            (frame "case.list" {| limit = 1 |})
            3

    Expect.equal
        (wrongState.GetProperty("kind").GetString())
        "localFailure"
        "Forged PKCE state refuses"

let private supplementary fixture =
    let reference = Guid.NewGuid().ToString("N") + "-unicode"
    let name = String.replicate 200 "🙂"

    let input =
        {|
            operationId = id ()
            caseReference = reference
            expectedRevision = 0L
            act =
                {|
                    kind = "OPEN"
                    values =
                        {|
                            incidentDate = "2026-08-01"
                            incidentNotificationDate = "2026-08-03"
                            incidentCountry = "LT"
                            claimantName = name
                            insurerName = name
                            claimedAmount = "1.0000"
                            claimedCurrency = "EUR"
                        |}
                |}
        |}

    automation fixture "case.preview" input 0 |> require "REVIEWED" |> ignore
    let receipt = automation fixture "case.execute" input 0 |> require "ACCEPTED"

    let observed =
        automation fixture "operation.observe" {| operationId = input.operationId |} 0
        |> require "SUCCEEDED"

    Expect.equal (observed.GetRawText()) (receipt.GetRawText()) "Supplementary exact observation"

    let replay = automation fixture "case.execute" input 0 |> require "ALREADY_ACCEPTED"

    Expect.equal
        (replay
            .GetProperty("snapshot")
            .GetProperty("state")
            .GetProperty("fields")
            .GetProperty("claimantName")
            .GetString())
        name
        "Scalar boundary exact"

let private qualify () =
    task {
        use fixture = new PublishedFixture.Fixture()
        do! fixture.Start()
        PublishedAssets.qualify fixture
        setup fixture
        CliDelivery.qualify fixture
        CliDisclosure.qualify fixture
        inputBoundaries fixture
        RejectedDrafts.qualify fixture
        let position = walkthrough fixture
        verify fixture position
        supplementary fixture
        stewardship fixture
        DifferentClient.qualify fixture
        fixture.Complete()
    }

[<Tests>]
let tests =
    testCaseAsync
        "[CC-CLI-001][CC-AUTH-001] published walkthrough, exact replay, roles, stewardship, Unicode and checkpoint"
        (async { do! qualify () |> Async.AwaitTask })
