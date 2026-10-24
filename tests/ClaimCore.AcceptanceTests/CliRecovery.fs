module internal ClaimCore.AcceptanceTests.CliRecovery

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open CliCalls

type private Submitted =
    {
        Bytes: byte array
        OperationId: string
        Reference: string
    }

let private authored (fixture: PublishedFixture.Fixture) =
    let source =
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture.Root, "examples/01-open.json")))
        |> nonNull

    let input = source["input"] |> nonNull
    let operation = id ()
    let reference = "CLI-RECOVERY-" + id ()
    input["operationId"] <- JsonValue.Create operation
    input["caseReference"] <- JsonValue.Create reference
    let values = (input["act"] |> nonNull)["values"] |> nonNull
    values["claimedAmount"] <- JsonValue.Create "1.0000"

    {
        Bytes = Encoding.UTF8.GetBytes(source.ToJsonString())
        OperationId = operation
        Reference = reference
    }

let private field name (value: JsonElement) =
    value.GetProperty(name: string).GetString() |> nonNull

let private head (fixture: PublishedFixture.Fixture) =
    let result = fixture.Administration [ "head" ]
    Expect.equal result.ExitCode 0 "Owner reads the actual ledger head"
    result.StandardOutput

let private pending (fixture: PublishedFixture.Fixture) =
    let items = ResizeArray<JsonElement>()
    let seen = Collections.Generic.HashSet<string>()
    let mutable cursor: string option = None
    let mutable more = true

    while more do
        let arguments =
            [ "operation"; "pending"; "--limit"; "50" ]
            @ (cursor
               |> Option.map (fun value -> [ "--cursor"; value ])
               |> Option.defaultValue [])

        let page = verb fixture fixture.Environment arguments 0 |> require "SUCCEEDED"

        for item in page.GetProperty("items").EnumerateArray() do
            items.Add(item.Clone())

        let next = page.GetProperty "nextCursor"

        if next.ValueKind = JsonValueKind.Null then
            more <- false
        else
            let value = next.GetString() |> nonNull
            Expect.isTrue (seen.Add value) "Recovery continuation advances without cycling"
            cursor <- Some value

    List.ofSeq items

let private selected fixture (submitted: Submitted) =
    let item =
        pending fixture
        |> List.tryFind (fun item -> field "operationId" item = submitted.OperationId)
        |> Option.defaultWith (fun () ->
            failtest "New CLI process must find the retained submission")

    Expect.equal (field "scope" item) "CASE" "Technical scope retained"
    Expect.equal (field "act" item) "OPEN" "Original act retained"
    Expect.equal (field "caseReference" item) submitted.Reference "Original target retained"
    Expect.equal (item.GetProperty("expectedRevision").GetInt64()) 0L "Original revision retained"

    let offers =
        item.GetProperty("offers").EnumerateArray()
        |> Seq.map (fun value -> value.GetString() |> nonNull)
        |> Seq.toList

    Expect.contains offers "RETRY" "Current actor is offered explicit retry"
    field "recoveryId" item

let private onlyAcceptance fixture (submitted: Submitted) (receipt: JsonElement) =
    Expect.equal
        (field "operationId" receipt)
        submitted.OperationId
        "Recovered acceptance keeps its operation ID"

    let scope = receipt.GetProperty "scope"
    Expect.equal (field "kind" scope) "CASE" "Case receipt"
    Expect.equal (field "act" scope) "OPEN" "No changed command"
    Expect.equal (field "caseReference" scope) submitted.Reference "No changed target"
    Expect.equal (scope.GetProperty("revision").GetInt64()) 1L "No implicit rebase"

    let current =
        automation
            fixture
            "case.get"
            {|
                caseReference = submitted.Reference
            |}
            0
        |> require "SUCCEEDED"

    Expect.equal (current.GetProperty("revision").GetInt64()) 1L "Exactly one current revision"

    let amount =
        current.GetProperty("state").GetProperty("fields").GetProperty("claimedAmount").GetString()

    Expect.isTrue (amount = "1") "Original authored amount retains its accepted decimal value"

    let history =
        automation
            fixture
            "case.history"
            {|
                caseReference = submitted.Reference
                detail = "FULL"
                limit = 50
            |}
            0
        |> require "SUCCEEDED"

    let entries = history.GetProperty "entries"
    Expect.equal (entries.GetArrayLength()) 1 "Retry appends exactly one acceptance in total"
    let historical = entries[0].GetProperty "receipt"

    Expect.equal
        (field "operationId" historical)
        submitted.OperationId
        "History identifies the same acceptance"

    Expect.isTrue
        (JsonElement.DeepEquals(historical.GetProperty "position", receipt.GetProperty "position"))
        "History and receipt have the same authoritative position"

let private acknowledged fixture (submitted: Submitted) =
    Expect.isFalse
        (pending fixture
         |> List.exists (fun item -> field "operationId" item = submitted.OperationId))
        "Complete known output acknowledges the original owned journal row"

let private journalLoss (fixture: PublishedFixture.Fixture) =
    task {
        let submitted = authored fixture
        let before = head fixture
        let intercepted = fixture.ArmJournalReplyLoss()
        let failed = run fixture fixture.Environment submitted.Bytes 3
        let fault = require "FAILED" failed

        Expect.equal
            (field "code" fault)
            "CORE_STORE_UNAVAILABLE"
            "Unconfirmed stage does not dispatch business execution"

        Expect.isTrue
            (intercepted.Wait(TimeSpan.FromSeconds 5.))
            "Actual journal COMMIT reply was withheld"

        Expect.equal (head fixture) before "Journal-only persistence appends no accepted entry"
        do! fixture.RestartWeb()
        let recoveryId = selected fixture submitted

        automation
            fixture
            "operation.observe"
            {|
                operationId = submitted.OperationId
            |}
            2
        |> require "REJECTED"
        |> ignore

        automation
            fixture
            "case.get"
            {|
                caseReference = submitted.Reference
            |}
            2
        |> require "REJECTED"
        |> ignore

        Expect.equal
            (head fixture)
            before
            "Restart and metadata enumeration never execute the submission"

        let reply =
            verb
                fixture
                fixture.Environment
                [ "operation"; "retry"; recoveryId; submitted.OperationId ]
                0

        let receipt = require "ACCEPTED" reply
        onlyAcceptance fixture submitted receipt
        acknowledged fixture submitted
    }

let private armResponseLoss (fixture: PublishedFixture.Fixture) =
    JsonSerializer.Serialize
        {|
            id = id ()
            path = "/api/operation.execute"
            status = RedirectControl.lostResponse
        |}
    |> FixtureFiles.text fixture.Directory "redirect-control.json"
    |> ignore

let private originalAcceptance (fixture: PublishedFixture.Fixture) =
    use evidence =
        JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(fixture.Directory, "redirect-evidence.json"))
        )

    let reply = evidence.RootElement.GetProperty "reply"

    Expect.equal
        (reply.GetProperty("outcome").GetProperty("tag").GetString())
        "ACCEPTED"
        "The real backend accepted before its HTTP reply was lost"

    reply.GetProperty("outcome").GetProperty("data").Clone()

let private responseLoss (fixture: PublishedFixture.Fixture) =
    task {
        let submitted = authored fixture
        armResponseLoss fixture
        let unknown = run fixture fixture.Environment submitted.Bytes 4

        Expect.equal
            (field "operationId" unknown)
            submitted.OperationId
            "Lost output retains operation identity"

        let original = originalAcceptance fixture
        do! fixture.RestartWeb()
        let recoveryId = selected fixture submitted

        let reply =
            automation
                fixture
                "operation.retry"
                {|
                    recoveryId = recoveryId
                    operationId = submitted.OperationId
                |}
                0

        let receipt = require "ALREADY_ACCEPTED" reply

        Expect.isTrue
            (JsonElement.DeepEquals(receipt, original))
            "Exact acceptance and authorized disclosure survive server and CLI replacement"

        onlyAcceptance fixture submitted receipt
        acknowledged fixture submitted
    }

let qualify fixture =
    task {
        do! journalLoss fixture
        do! responseLoss fixture
    }
