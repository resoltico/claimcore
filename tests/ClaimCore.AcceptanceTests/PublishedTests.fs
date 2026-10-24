module ClaimCore.AcceptanceTests.PublishedTests

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open CliCalls

let private verbArguments verb (bytes: byte array) =
    use source = JsonDocument.Parse(ReadOnlyMemory<byte> bytes)
    let input = source.RootElement.GetProperty "input"
    let act = input.GetProperty "act"

    let flag (value: JsonProperty) =
        let name =
            System.Text.RegularExpressions.Regex
                .Replace(value.Name, "([a-z0-9])([A-Z])", "$1-$2")
                .ToLowerInvariant()

        [ "--" + name; value.Value.GetString() |> nonNull ]

    let values (element: JsonElement) =
        element.EnumerateObject() |> Seq.collect flag |> Seq.toList

    let details =
        let mutable element = Unchecked.defaultof<JsonElement>

        if act.TryGetProperty("values", &element) then
            values element
        elif act.TryGetProperty("reason", &element) then
            [ "--reason"; element.GetString() |> nonNull ]
        else
            []

    let revision =
        if verb = "open" then
            []
        else
            [ "--expected-revision"; input.GetProperty("expectedRevision").GetRawText() ]

    [
        "case"
        verb
        input.GetProperty("caseReference").GetString() |> nonNull
        "--operation-id"
        input.GetProperty("operationId").GetString() |> nonNull
    ]
    @ revision
    @ details

let private assertDryRun (fixture: PublishedFixture.Fixture) arguments expected =
    let result =
        ProcessRunner.dotnet
            8_000
            fixture.Cli
            (arguments @ [ "--dry-run" ])
            (Collections.Generic.Dictionary<string, string>())
            None

    Expect.equal result.ExitCode 0 "Published dry run requires no configuration or authentication"
    Expect.equal result.StandardError.Length 0 "No dry-run diagnostics"
    use actual = JsonDocument.Parse(ReadOnlyMemory<byte> result.StandardOutput)
    use authored = JsonDocument.Parse(ReadOnlyMemory<byte> expected)

    Expect.isTrue
        (JsonElement.DeepEquals(actual.RootElement, authored.RootElement))
        "Verb prepares the independently authored call frame"

let private exactReply (fixture: PublishedFixture.Fixture) name expected actual =
    if actual <> expected then
        FixtureFiles.text fixture.Directory (name + ".expected.json") expected |> ignore
        FixtureFiles.text fixture.Directory (name + ".actual.json") actual |> ignore

    Expect.isTrue (actual = expected) "Exact reply content agrees; mismatch evidence stays private"

let private walkthrough (fixture: PublishedFixture.Fixture) =
    let files =
        [
            "01-open.json", "open"
            "02-decide.json", "decide"
            "03-record-payment.json", "record-payment"
            "04-close.json", "close"
            "05-reopen.json", "reopen"
            "06-correct-payment-record.json", "clear-payment"
        ]

    let mutable position = Unchecked.defaultof<JsonElement>

    for index, (file, act) in List.indexed files do
        let bytes = File.ReadAllBytes(Path.Combine(fixture.Root, "examples", file))
        let arguments = verbArguments act bytes
        assertDryRun fixture arguments bytes
        let receipt = verb fixture fixture.Environment arguments 0 |> require "ACCEPTED"

        Expect.equal
            (receipt.GetProperty("scope").GetProperty("revision").GetInt64())
            (int64 (index + 1))
            "Example revision"

        let replay =
            verb fixture fixture.Environment arguments 0 |> require "ALREADY_ACCEPTED"

        exactReply fixture "walkthrough-replay" (receipt.GetRawText()) (replay.GetRawText())
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
        let snapshot = receipt.GetProperty("scope").GetProperty("snapshot")

        Expect.equal
            (snapshot.GetProperty("disclosure").GetString())
            "ERASED"
            "Business payload is erased"

        Expect.equal
            (snapshot.EnumerateObject() |> Seq.length)
            1
            "No historical payload retained in receipt"

        let names = receipt.EnumerateObject() |> Seq.map _.Name |> Seq.sort |> Seq.toList

        Expect.equal
            names
            [ "operationId"; "position"; "recordedAt"; "recordedBy"; "scope" ]
            "Receipt retains identity and ledger metadata"

let private erasedView (tombstone: JsonElement) =
    Expect.equal
        (tombstone.GetProperty("state").GetProperty("tag").GetString())
        "ERASED"
        "Erased state"

    let names = tombstone.EnumerateObject() |> Seq.map _.Name |> Seq.sort |> Seq.toList

    Expect.equal
        names
        [ "advice"; "caseReference"; "revision"; "state" ]
        "Erased views expose only identity, revision, state and current advice"

    let advice = tombstone.GetProperty "advice"

    Expect.equal
        (advice.GetProperty("acts").GetArrayLength())
        0
        "Erased records offer no business or stewardship act"

    let history =
        advice.GetProperty("history").EnumerateArray()
        |> Seq.map (fun detail -> detail.GetString() |> nonNull)
        |> Seq.sort
        |> Seq.toList

    Expect.equal
        history
        [ "FULL"; "SUMMARY" ]
        "Only authorized retained history details are offered"


let private stewardship (fixture: PublishedFixture.Fixture) =
    let source =
        Path.Combine(fixture.Root, "examples", "07-void.json") |> File.ReadAllBytes

    let arguments = verbArguments "void" source
    assertDryRun fixture arguments source

    let blocked =
        verb fixture (interactiveEnvironment fixture "synthetic-editor" "valid") arguments 2

    Expect.equal (tag blocked) "REJECTED" "Editor cannot steward"

    verb fixture (interactiveEnvironment fixture "synthetic-steward" "valid") arguments 0
    |> require "ACCEPTED"
    |> ignore

    for file, act in [ "08-reinstate.json", "reinstate"; "09-erase.json", "erase" ] do
        let bytes = File.ReadAllBytes(Path.Combine(fixture.Root, "examples", file))
        let arguments = verbArguments act bytes
        assertDryRun fixture arguments bytes

        verb fixture (interactiveEnvironment fixture "synthetic-steward" "valid") arguments 0
        |> require "ACCEPTED"
        |> ignore

    let erased =
        automation fixture "case.get" {| caseReference = "DEMO-0001" |} 0
        |> require "SUCCEEDED"

    erasedView erased
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

let private requireWrongStateDelivery
    (environment: System.Collections.Generic.IReadOnlyDictionary<string, string>)
    =
    let progress = environment["CLAIMCORE_CLI_TEST_PROGRESS_FILE"]
    let timer = System.Diagnostics.Stopwatch.StartNew()
    let mutable stage = CliInvocationEvidence.readStage progress

    while stage <> "wrong-state-delivered" && timer.Elapsed < TimeSpan.FromSeconds 5. do
        System.Threading.Thread.Sleep 10
        stage <- CliInvocationEvidence.readStage progress

    Expect.equal
        stage
        "wrong-state-delivered"
        ("The provider-issued code must reach the wrong-state control; browserReturn="
         + CliInvocationEvidence.browserReturn (Some progress))

let private inputBoundaries fixture =
    let malformed =
        Encoding.UTF8.GetBytes "{\"endpoint\":\"case.list\",\"input\":{\"limit\":1},\"extra\":true}"

    let reply = run fixture fixture.Environment malformed 2

    Expect.equal
        (reply.GetProperty("result").GetProperty("localFailure").GetProperty("code").GetString())
        "INVALID_FRAME"
        "Strict frame rejects extra property"

    let reader = human fixture "synthetic-reader" "case.list" {| limit = 50 |} 0
    require "SUCCEEDED" reader |> ignore

    let authored =
        File.ReadAllBytes(Path.Combine(fixture.Root, "examples", "01-open.json"))

    let before = fixture.Administration [ "head" ]
    Expect.equal before.ExitCode 0 "Accepted state before unauthorized input"

    let refused =
        run fixture (interactiveEnvironment fixture "synthetic-reader" "valid") authored 2

    Expect.equal (tag refused) "REJECTED" "Reader cannot execute"
    let after = fixture.Administration [ "head" ]
    Expect.equal after.ExitCode 0 "Accepted state after unauthorized input"
    Expect.equal before.StandardOutput after.StandardOutput "Unauthorized input appends nothing"

    Expect.equal
        (fixture.Administration [ "verify" ]).ExitCode
        0
        "Unauthorized input changes no projection"

    let environment = interactiveEnvironment fixture "synthetic-owner" "wrong-state"

    let wrongState = run fixture environment (frame "case.list" {| limit = 1 |}) 3

    requireWrongStateDelivery environment

    Expect.equal
        (wrongState
            .GetProperty("result")
            .GetProperty("localFailure")
            .GetProperty("code")
            .GetString())
        "CLI_AUTHENTICATION_FAILED"
        "Forged PKCE state refuses"

let private supplementary fixture =
    let reference = Guid.NewGuid().ToString("N") + "-unicode"
    let name = String.replicate 200 "🙂"

    let input =
        {|
            scope = "CASE"
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

    automation fixture "operation.preview" input 0 |> require "REVIEWED" |> ignore
    let receipt = automation fixture "operation.execute" input 0 |> require "ACCEPTED"

    let observed =
        automation fixture "operation.observe" {| operationId = input.operationId |} 0
        |> require "SUCCEEDED"

    exactReply fixture "supplementary-observation" (receipt.GetRawText()) (observed.GetRawText())

    let replay =
        automation fixture "operation.execute" input 0 |> require "ALREADY_ACCEPTED"

    let actualName =
        replay
            .GetProperty("scope")
            .GetProperty("snapshot")
            .GetProperty("view")
            .GetProperty("fields")
            .GetProperty("claimantName")
            .GetString()

    Expect.isTrue (actualName = name) "Scalar boundary exact without public business-text output"

let private qualify () =
    task {
        use fixture = new PublishedFixture.Fixture()
        do! fixture.Start()
        PublishedAssets.qualify fixture
        CliDiscovery.qualify fixture
        setup fixture
        CliNativeOutput.qualify fixture
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
