module internal ClaimCore.AcceptanceTests.CliDelivery

open ClaimCore.Contract

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Contract.Protocol
open CliCalls

let private arm (fixture: PublishedFixture.Fixture) status =
    let body =
        JsonSerializer.Serialize
            {|
                id = id ()
                path = "/api/case.execute"
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

let private field (name: string) (reply: JsonElement) =
    reply.GetProperty(name).GetString() |> nonNull

/// This is the automation caller's state. Each CLI process only reports its own attempt.
let private knowledge token =
    Codec.decode 128 Frames.knowledge (JsonSerializer.SerializeToUtf8Bytes(token: string))
    |> Result.defaultWith (fun _ -> failtest "Unknown attempt token")

let private accumulated token =
    if token = "CONTENT_EXCLUDED" then
        AccumulatedKnowledge.ContentExcluded
    else
        AccumulatedKnowledge.Known(knowledge token)

let private retain prior expected attempt evidence reply =
    Expect.equal (field "attemptKnowledge" reply) attempt "Published per-attempt knowledge"
    Expect.equal (field "resolutionEvidence" reply) evidence "Published content evidence"

    let resolution =
        Codec.decode 128 Frames.evidence (JsonSerializer.SerializeToUtf8Bytes(evidence: string))
        |> Result.defaultWith (fun _ -> failtest "Unknown evidence token")

    let next = Delivery.accumulate (accumulated prior) (knowledge attempt) resolution

    Expect.equal
        next
        (accumulated expected)
        "Caller accumulation preserves unresolved earlier delivery"

    expected

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

    Expect.equal
        (page.GetProperty("entries").GetArrayLength())
        1
        "Exact retries and refusals append no second ledger entry"

    let current =
        automation fixture "case.get" {| caseReference = reference |} 0
        |> require "SUCCEEDED"

    Expect.equal
        (current.GetProperty("revision").GetInt64())
        1L
        "Projection remains at the single accepted revision"

    let amount =
        current.GetProperty("state").GetProperty("fields").GetProperty("claimedAmount").GetString()

    Expect.equal amount "1" "Accepted payload is the original amount"

let private interruptedRetries fixture source operationId =
    arm fixture RedirectControl.lostResponse
    let lost = run fixture fixture.Environment source 4

    Expect.equal
        (field "operationId" lost)
        operationId
        "Unconfirmed frame retains the original operation ID"

    let pending = retain "NOT_SENT" "UNCONFIRMED" "UNCONFIRMED" "NONE" lost
    let invalid = Dictionary<string, string>(fixture.Environment)
    invalid.Remove("CLAIMCORE_SERVICE_URL") |> ignore
    let local = run fixture invalid source 3
    let pending = retain pending "UNCONFIRMED" "NOT_SENT" "NONE" local
    arm fixture RedirectControl.beforeDispatchFailure
    let host = run fixture fixture.Environment source 3
    let pending = retain pending "UNCONFIRMED" "NOT_ACCEPTED" "NONE" host

    Expect.equal
        (host.GetProperty("failure").GetProperty("executionPhase").GetString())
        "NOT_STARTED"
        "Host phase is serialized"

    let denied =
        run fixture (interactiveEnvironment fixture "synthetic-reader" "valid") source 2

    retain pending "UNCONFIRMED" "NOT_ACCEPTED" "NONE" denied

let private exactResolution fixture (source: byte array) operationId pending =
    let observed =
        automation fixture "operation.observe" {| operationId = operationId |} 0

    let pending = retain pending "UNCONFIRMED" "READ_SUCCEEDED" "NONE" observed
    use document = JsonDocument.Parse(ReadOnlyMemory source)

    let preview =
        automation fixture "case.preview" (document.RootElement.GetProperty "input") 0

    require "ALREADY_ACCEPTED" preview |> ignore
    retain pending "ACCEPTED" "ACCEPTED" "CONTENT_ACCEPTED" preview |> ignore
    let replay = run fixture fixture.Environment source 0
    require "ALREADY_ACCEPTED" replay |> ignore
    retain pending "ACCEPTED" "ACCEPTED" "CONTENT_ACCEPTED" replay |> ignore

let private differentContent fixture source operationId =
    let changed = JsonNode.Parse(source: byte array) |> nonNull

    let values =
        (((changed["input"] |> nonNull)["act"] |> nonNull)["values"] |> nonNull)

    values["claimedAmount"] <- JsonValue.Create "2.0000"
    let different = Encoding.UTF8.GetBytes(changed.ToJsonString())
    arm fixture RedirectControl.lostResponse
    let lost = run fixture fixture.Environment different 4
    let pending = retain "NOT_SENT" "UNCONFIRMED" "UNCONFIRMED" "NONE" lost

    let observed =
        automation fixture "operation.observe" {| operationId = operationId |} 0

    let pending = retain pending "UNCONFIRMED" "READ_SUCCEEDED" "NONE" observed
    let excluded = run fixture fixture.Environment different 2
    let refusal = require "REJECTED" excluded

    Expect.equal
        (refusal.GetProperty("code").GetString())
        "OPERATION_CONTENT_CONFLICT"
        "Immutable ID excludes this different content"

    retain pending "CONTENT_EXCLUDED" "NOT_ACCEPTED" "CONTENT_EXCLUDED" excluded
    |> ignore

let private coreUnknown (fixture: PublishedFixture.Fixture) =
    let source, operationId, reference = authored fixture
    let intercepted = fixture.ArmCommitReplyLoss()
    automation fixture "context" {| |} 0 |> require "SUCCEEDED" |> ignore
    Expect.isFalse intercepted.IsCompleted "An armed mutation fault never drops read commits"
    let unknown = run fixture fixture.Environment source 4
    let outcome = require "OUTCOME_UNKNOWN" unknown

    Expect.equal
        (field "operationId" outcome)
        operationId
        "Core uncertainty retains the submitted identity"

    Expect.isTrue
        (intercepted.Wait(TimeSpan.FromSeconds 5.))
        "Actual PostgreSQL COMMIT completion was withheld"

    let pending = retain "NOT_SENT" "UNCONFIRMED" "UNCONFIRMED" "NONE" unknown
    let replay = run fixture fixture.Environment source 0
    require "ALREADY_ACCEPTED" replay |> ignore
    retain pending "ACCEPTED" "ACCEPTED" "CONTENT_ACCEPTED" replay |> ignore
    oneAcceptance fixture reference

let qualify fixture =
    let source, operationId, reference = authored fixture
    let retained = Array.copy source
    let pending = interruptedRetries fixture source operationId
    exactResolution fixture source operationId pending
    differentContent fixture source operationId
    oneAcceptance fixture reference
    Expect.equal source retained "Caller never rewrites its original authored bytes"
    coreUnknown fixture
