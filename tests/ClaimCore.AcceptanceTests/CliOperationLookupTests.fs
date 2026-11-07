module ClaimCore.AcceptanceTests.CliOperationLookupTests

open ClaimCore.Json

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open CliCalls
open ReceiptObservations

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
    let reference = "CLI-RESOLUTION-" + id ()
    input["operationId"] <- JsonValue.Create operation
    (input["target"] |> nonNull)["caseReference"] <- JsonValue.Create reference
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

let private onlyAcceptance fixture (submitted: Submitted) (receipt: JsonElement) =
    Expect.equal
        (field "operationId" receipt)
        submitted.OperationId
        "Recovered acceptance keeps its operation ID"

    let subject = receipt.GetProperty "subject"
    Expect.equal (field "kind" subject) "CASE" "Case receipt"
    Expect.equal (field "act" subject) "OPEN" "No changed act"
    Expect.equal (field "caseReference" subject) submitted.Reference "No changed target"
    Expect.equal (subject.GetProperty("revision").GetInt64()) 1L "No implicit rebase"

    let current =
        automation
            fixture
            "case.get"
            {|
                target =
                    {|
                        kind = "REFERENCE"
                        caseReference = submitted.Reference
                    |}
            |}
            0
        |> require "SUCCEEDED"

    Expect.equal (current.GetProperty("revision").GetInt64()) 1L "Exactly one current revision"

    let amount =
        current
            .GetProperty("state")
            .GetProperty("fields")
            .GetProperty("registration")
            .GetProperty("claimedAmount")
            .GetString()

    Expect.isTrue (amount = "1") "Original authored amount retains its accepted decimal value"

    let history =
        automation
            fixture
            "case.history"
            {|
                target =
                    {|
                        kind = "REFERENCE"
                        caseReference = submitted.Reference
                    |}
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
        (reply.GetProperty("outcome").GetProperty("kind").GetString())
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

        let receipt =
            sameRecordedReceipt fixture "restart-lookup-receipt" original (fun () ->
                lookupReceipt fixture submitted.OperationId)

        onlyAcceptance fixture submitted receipt
    }

let private idlessCall (fixture: PublishedFixture.Fixture) =
    let source =
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture.Root, "examples/01-open.json")))
        |> nonNull

    let input = source["input"] |> nonNull
    Expect.isNull input["operationId"] "The public example omits its operation identity"
    (input["target"] |> nonNull)["caseReference"] <- JsonValue.Create("CLI-IDLESS-" + id ())
    let bytes = Encoding.UTF8.GetBytes(source.ToJsonString())
    let result = invoke fixture fixture.Environment [ "call" ] bytes 0
    let reply = parse 0 result
    let receipt = require "ACCEPTED" reply
    let operation = receipt.GetProperty("operationId").GetString() |> nonNull

    Expect.equal
        (Encoding.UTF8.GetString result.StandardError)
        (operation + "\n")
        "Dispatch publishes its final UUID on stderr"

    Expect.isSome
        (ClaimCore.Domain.OperationId.create (Guid.Parse operation))
        "The generated ID is UUIDv7"

    input["operationId"] <- JsonValue.Create operation
    let retry = Encoding.UTF8.GetBytes(source.ToJsonString())

    sameRecordedReceipt fixture "idless-exact-replay" receipt (fun () ->
        run fixture fixture.Environment retry 0 |> require "ALREADY_ACCEPTED")
    |> ignore

let private qualify fixture =
    task {
        do! responseLoss fixture
        idlessCall fixture
        let before = head fixture
        let operation = id ()

        let pending =
            verb fixture fixture.Environment [ "operation"; "get"; operation ] 4
            |> require "PENDING"

        let stamp = Convert.ToInt64(operation.Replace("-", "").Substring(0, 12), 16)

        let deadline =
            DateTimeOffset.Parse(
                pending.GetProperty("finalAt").GetString() |> nonNull,
                Globalization.CultureInfo.InvariantCulture
            )

        Expect.equal
            deadline
            (DateTimeOffset.FromUnixTimeMilliseconds(stamp).AddMinutes(10.))
            "Published lifetime deadline"

        let expired =
            Guid.CreateVersion7(DateTimeOffset.UtcNow.AddMinutes(-11.)).ToString("D")

        for _ in 1..2 do
            verb fixture fixture.Environment [ "operation"; "get"; expired ] 2
            |> require "NOT_ACCEPTED"
            |> ignore

        Expect.equal (head fixture) before "Pending reads and final locked absence append nothing"
    }

[<Tests>]
let tests =
    testCaseAsync
        "published CLI recovers bounded operation attempts"
        (async {
            do!
                task {
                    use fixture = new PublishedFixture.Fixture()
                    do! fixture.Start()
                    setup fixture
                    do! qualify fixture
                    fixture.Complete()
                }
                |> Async.AwaitTask
        })
