module internal ClaimCore.AcceptanceTests.CliResolution

open ClaimCore.Json

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
    let reference = "CLI-RESOLUTION-" + id ()
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
                caseReference = submitted.Reference
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

        let reply =
            verb fixture fixture.Environment [ "operation"; "resolve"; submitted.OperationId ] 0

        let receipt = require "ACCEPTED" reply

        Expect.isTrue
            (JsonElement.DeepEquals(receipt, original))
            "Exact acceptance and authorized disclosure survive server and CLI replacement"

        onlyAcceptance fixture submitted receipt
    }

let qualify fixture =
    task {
        let retired =
            invoke fixture fixture.Environment [ "call"; "--retry" ] Array.empty 64

        Expect.isEmpty retired.StandardOutput "The retired flag is refused before reading stdin"
        do! responseLoss fixture
        let operationId = id ()
        let before = head fixture

        verb fixture fixture.Environment [ "operation"; "resolve"; operationId ] 2
        |> require "NOT_ACCEPTED"
        |> ignore

        Expect.equal (head fixture) before "Resolution appends no ledger entry"
    }
