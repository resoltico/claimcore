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

let private pendingWith (fixture: PublishedFixture.Fixture) environment =
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

        let page = verb fixture environment arguments 0 |> require "SUCCEEDED"

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

let private pending (fixture: PublishedFixture.Fixture) = pendingWith fixture fixture.Environment

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

let private originalReply (fixture: PublishedFixture.Fixture) =
    use evidence =
        JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(fixture.Directory, "redirect-evidence.json"))
        )

    evidence.RootElement.GetProperty("reply").Clone()

let private originalAcceptance fixture =
    let reply = originalReply fixture

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

let private steward fixture =
    interactiveEnvironment fixture "synthetic-steward" "valid"

let private retainOtherActor (fixture: PublishedFixture.Fixture) opened =
    let source = JsonNode.Parse(opened.Bytes: byte array) |> nonNull
    let input = source["input"] |> nonNull
    let operation = id ()
    input["operationId"] <- JsonValue.Create operation
    input["expectedRevision"] <- JsonValue.Create 1L
    let act = input["act"] |> nonNull
    act["kind"] <- JsonValue.Create "AMEND_REGISTRATION"
    let values = act["values"] |> nonNull
    values["claimantName"] <- JsonValue.Create "Synthetic unconfirmed correction"
    let intercepted = fixture.ArmJournalReplyLoss()

    run fixture fixture.Environment (Encoding.UTF8.GetBytes(source.ToJsonString())) 3
    |> require "FAILED"
    |> ignore

    Expect.isTrue
        (intercepted.Wait(TimeSpan.FromSeconds 5.))
        "Other actor's protected case body was journaled without acceptance"

    Expect.isTrue
        (pending fixture
         |> List.exists (fun item -> field "operationId" item = operation))
        "The other actor retains a case recovery copy before erasure"

let private eraseRequest fixture =
    let opened = authored fixture
    run fixture fixture.Environment opened.Bytes 0 |> require "ACCEPTED" |> ignore
    retainOtherActor fixture opened
    let operation = id ()

    { opened with
        OperationId = operation
        Bytes =
            frame
                "operation.execute"
                {|
                    scope = "CASE"
                    operationId = operation
                    caseReference = opened.Reference
                    expectedRevision = 1L
                    act = {| kind = "ERASE" |}
                |}
    }

let private stageErasure (fixture: PublishedFixture.Fixture) submitted =
    let intercepted = fixture.ArmJournalReplyLoss()
    run fixture (steward fixture) submitted.Bytes 3 |> require "FAILED" |> ignore

    Expect.isTrue
        (intercepted.Wait(TimeSpan.FromSeconds 5.))
        "Erasure journal persisted before its confirmation was lost"

    let row =
        pendingWith fixture (steward fixture)
        |> List.find (fun item -> field "operationId" item = submitted.OperationId)

    Expect.equal (field "act" row) "ERASE" "Original erasure metadata retained"

    Expect.equal
        (row.GetProperty("expectedRevision").GetInt64())
        1L
        "Erasure retains its original expected revision"

    field "recoveryId" row

let private savedRetry submitted (result: ProcessRunner.Result) =
    let reply = parse 4 result

    Expect.equal
        (field "operationId" reply)
        submitted.OperationId
        "Uncertainty retains the original erasure identity"

    let lines =
        UTF8Encoding(false, true).GetString(result.StandardError).TrimEnd('\n').Split('\n')

    let saved = Encoding.UTF8.GetBytes lines[lines.Length - 1]

    let canonical bytes =
        ClaimCore.Contract.Frames.admit bytes
        |> Result.defaultWith (fun _ -> failtest "Owned erasure frame must be admitted")
        |> ClaimCore.Contract.Frames.encodeInvocation

    Expect.isTrue
        (saved = canonical submitted.Bytes)
        "The actual stderr frame preserves exact frozen erasure input"

    saved

let private erasureGone fixture submitted =
    let rows = pendingWith fixture (steward fixture)

    Expect.isFalse
        (rows
         |> List.exists (fun item -> field "caseReference" item = submitted.Reference))
        "Both actors case recovery copies, including erasure itself, are removed"

    let tombstone =
        human
            fixture
            "synthetic-steward"
            "case.get"
            {|
                caseReference = submitted.Reference
            |}
            0
        |> require "SUCCEEDED"

    Expect.equal
        (tombstone.GetProperty("state").GetProperty("tag").GetString())
        "ERASED"
        "Observed tombstone proves erasure, not frozen byte equality"

    Expect.equal
        (tombstone.GetProperty("revision").GetInt64())
        2L
        "Erasure appended exactly one revision"

    let observed =
        human
            fixture
            "synthetic-steward"
            "operation.observe"
            {|
                operationId = submitted.OperationId
            |}
            0
        |> require "SUCCEEDED"

    Expect.equal
        (observed.GetProperty("scope").GetProperty("snapshot").GetProperty("disclosure").GetString())
        "ERASED"
        "Observation exposes no erased payload"

let private eraseResponseLoss (fixture: PublishedFixture.Fixture) =
    task {
        let submitted = eraseRequest fixture
        let recoveryId = stageErasure fixture submitted
        armResponseLoss fixture
        let lost = invoke fixture (steward fixture) [ "call" ] submitted.Bytes 4
        let saved = savedRetry submitted lost
        let accepted = originalAcceptance fixture

        Expect.equal
            ((originalReply fixture).GetProperty("recoveryId").ValueKind)
            JsonValueKind.Null
            "Erasure returns no retained recovery locator"

        Expect.equal
            (field "act" (accepted.GetProperty "scope"))
            "ERASE"
            "Real backend accepted erasure before reply loss"

        do! fixture.RestartWeb()
        erasureGone fixture submitted
        let before = head fixture
        let unresolved = invoke fixture (steward fixture) [ "call"; "--retry" ] saved 4
        savedRetry submitted unresolved |> ignore

        let opaque =
            human
                fixture
                "synthetic-steward"
                "operation.retry"
                {|
                    recoveryId = recoveryId
                    operationId = submitted.OperationId
                |}
                4

        Expect.equal
            (field "operationId" opaque)
            submitted.OperationId
            "Missing recovery cannot replace the original identity"

        Expect.equal
            (head fixture)
            before
            "Neither retry nor observation restores erased bytes or appends acceptance"

        erasureGone fixture submitted
    }

let qualify fixture =
    task {
        do! journalLoss fixture
        do! responseLoss fixture
        do! eraseResponseLoss fixture
    }
