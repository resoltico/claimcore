module internal ClaimCore.AcceptanceTests.CliDelivery

open ClaimCore.Cli

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Domain
open ClaimCore.Protocol
open ClaimCore.Json
open CliCalls

let private arm (fixture: PublishedFixture.Fixture) status =
    let body =
        JsonSerializer.Serialize
            {|
                id = id ()
                path = "/api/operation.execute"
                status = status
            |}

    FixtureFiles.text fixture.Directory "redirect-control.json" body |> ignore

let private authored (fixture: PublishedFixture.Fixture) =
    let source =
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture.Root, "examples/01-open.json")))
        |> nonNull

    let input = source["input"] |> nonNull
    let operationId = id ()
    let reference = "CLI-DELIVERY-" + id ()
    input["operationId"] <- JsonValue.Create operationId
    input["caseReference"] <- JsonValue.Create reference
    let values = (input["act"] |> nonNull)["values"] |> nonNull
    values["claimedAmount"] <- JsonValue.Create "1.0000"
    Encoding.UTF8.GetBytes(source.ToJsonString()), operationId, reference

let private field name (reply: JsonElement) =
    reply.GetProperty(name: string).GetString() |> nonNull

let private uncertain reply =
    Expect.equal (field "delivery" reply) "UNCERTAIN" "Prior commit remains unresolved"

let private exactBytes (fixture: PublishedFixture.Fixture) name expected actual =
    if actual <> expected then
        FixtureFiles.write fixture.Directory (name + ".expected.json") expected
        |> ignore

        FixtureFiles.write fixture.Directory (name + ".actual.json") actual |> ignore

    Expect.isTrue
        (actual = expected)
        "Prepared request bytes remain exactly equal; mismatch evidence stays private"

let private lostFrame (fixture: PublishedFixture.Fixture) source =
    let result =
        ProcessRunner.dotnet 90_000 fixture.Cli [ "call" ] fixture.Environment (Some source)

    let reply = parse 4 result
    uncertain reply
    let lines = Encoding.UTF8.GetString(result.StandardError).TrimEnd('\n').Split('\n')
    let retry = Encoding.UTF8.GetBytes lines[lines.Length - 1]

    let prepared =
        Frames.admit source
        |> Result.defaultWith (fun _ -> failtest "Synthetic source is admitted")
        |> Frames.encodeInvocation

    exactBytes fixture "stderr-retry-frame" prepared retry
    reply, retry

let private oneAcceptance fixture reference =
    let page =
        automation
            fixture
            "case.history"
            {|
                caseReference = reference
                detail = "FULL"
                limit = 50
            |}
            0
        |> require "SUCCEEDED"

    Expect.equal (page.GetProperty("entries").GetArrayLength()) 1 "Retries append no second entry"

    let current =
        automation fixture "case.get" {| caseReference = reference |} 0
        |> require "SUCCEEDED"

    Expect.equal (current.GetProperty("revision").GetInt64()) 1L "One accepted revision"

    let amount =
        current
            .GetProperty("state")
            .GetProperty("fields")
            .GetProperty("registration")
            .GetProperty("claimedAmount")
            .GetString()

    Expect.equal amount "1" "Accepted payload is the original amount"

let private interruptedRetries fixture source operationId =
    arm fixture RedirectControl.lostResponse
    let lost, retryFrame = lostFrame fixture source
    Expect.equal (field "operationId" lost) operationId "Original operation ID survives loss"
    let invalid = Dictionary<string, string>(fixture.Environment)
    invalid.Remove("CLAIMCORE_SERVICE_URL") |> ignore
    let notSent = run fixture invalid retryFrame 3
    Expect.equal (field "delivery" notSent) "NOT_SENT" "Only this attempt is classified"
    arm fixture RedirectControl.beforeDispatchFailure
    let host = run fixture fixture.Environment retryFrame 3

    Expect.equal
        (field "delivery" host)
        "DELIVERED"
        "The host reports a definite failure of this attempt"

    Expect.equal
        (host.GetProperty("result").GetProperty("hostFailure").GetProperty("phase").GetString())
        "NOT_STARTED"
        "Later predispatched refusal cannot disprove the earlier commit"

    run fixture (interactiveEnvironment fixture "synthetic-reader" "valid") retryFrame 2
    |> require "REJECTED"
    |> ignore

    retryFrame

let private exactResolution fixture source operationId =
    let observed =
        automation fixture "operation.observe" {| operationId = operationId |} 0

    require "SUCCEEDED" observed |> ignore
    // Observation establishes identity, never equality with pending content.
    let replay = run fixture fixture.Environment source 0
    require "ALREADY_ACCEPTED" replay |> ignore

    Expect.equal
        (field "delivery" replay)
        "DELIVERED"
        "Exact replay acceptance resolves uncertainty"

    use document = JsonDocument.Parse(ReadOnlyMemory(source: byte array))

    automation fixture "operation.preview" (document.RootElement.GetProperty "input") 0
    |> require "ALREADY_ACCEPTED"
    |> ignore

let private differentContent fixture source =
    let changed = JsonNode.Parse(source: byte array) |> nonNull

    let values =
        (((changed["input"] |> nonNull)["act"] |> nonNull)["values"] |> nonNull)

    values["claimedAmount"] <- JsonValue.Create "2.0000"
    let different = Encoding.UTF8.GetBytes(changed.ToJsonString())
    arm fixture RedirectControl.lostResponse
    let _, pending = lostFrame fixture different
    let conflict = run fixture fixture.Environment pending 2

    Expect.equal
        (field "delivery" conflict)
        "DELIVERED"
        "This attempt was refused; the earlier lost reply still requires resolution"

    Expect.equal
        ((require "REJECTED" conflict).GetProperty("code").GetString())
        "IDEMPOTENCY_CONFLICT"
        "Even a conflict cannot disprove acceptance of an earlier uncertain request"

let private coreUnknown (fixture: PublishedFixture.Fixture) =
    let source, operationId, reference = authored fixture
    let intercepted = fixture.ArmCommitReplyLoss()

    verb
        fixture
        (interactiveEnvironment fixture "synthetic-owner" "valid")
        [ "authority"; "list" ]
        0
    |> require "SUCCEEDED"
    |> ignore

    Expect.isFalse intercepted.IsCompleted "Armed mutation fault never drops read commits"
    let unknown, retryFrame = lostFrame fixture source
    require "OUTCOME_UNKNOWN" unknown |> ignore
    Expect.equal (field "operationId" unknown) operationId "Core uncertainty retains identity"
    Expect.isTrue (intercepted.Wait(TimeSpan.FromSeconds 5.)) "Actual COMMIT confirmation withheld"

    run fixture fixture.Environment retryFrame 0
    |> require "ALREADY_ACCEPTED"
    |> ignore

    oneAcceptance fixture reference

let qualify fixture =
    let source, operationId, reference = authored fixture
    let retained = Array.copy source
    let pending = interruptedRetries fixture source operationId
    exactResolution fixture pending operationId
    differentContent fixture source
    oneAcceptance fixture reference
    exactBytes fixture "authored-request" retained source
    coreUnknown fixture
