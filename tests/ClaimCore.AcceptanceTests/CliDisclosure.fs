module internal ClaimCore.AcceptanceTests.CliDisclosure

open ClaimCore.Json

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open CliCalls

let private original (fixture: PublishedFixture.Fixture) =
    let source =
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture.Root, "examples/01-open.json")))
        |> nonNull

    let input = source["input"] |> nonNull
    let reference = "CLI-DISCLOSURE-" + id ()
    input["operationId"] <- JsonValue.Create(id ())
    input["caseReference"] <- JsonValue.Create reference
    Encoding.UTF8.GetBytes(source.ToJsonString()), input, reference

let private amend (input: JsonNode) =
    let pending = input.DeepClone()
    pending["operationId"] <- JsonValue.Create(id ())
    pending["expectedRevision"] <- JsonValue.Create 1L
    let act = pending["act"] |> nonNull
    act["kind"] <- JsonValue.Create "AMEND_REGISTRATION"
    let values = act["values"] |> nonNull
    values["claimedAmount"] <- JsonValue.Create "2.0000"
    pending

let private staleReview fixture input reference =
    let pending = amend input
    automation fixture "operation.preview" pending 0 |> require "REVIEWED" |> ignore

    let close =
        {|
            kind = "CASE"
            operationId = id ()
            caseReference = reference
            expectedRevision = 1L
            act = {| kind = "CLOSE" |}
        |}

    automation fixture "operation.execute" close 0 |> require "ACCEPTED" |> ignore
    let refused = automation fixture "operation.execute" pending 2 |> require "REJECTED"

    Expect.equal
        (refused.GetProperty("code").GetString())
        "REVISION_CONFLICT"
        "Preview cannot authorize a stale write"

    Expect.equal
        (refused.GetProperty("actualRevision").GetInt64())
        2L
        "Current revision is authoritative"

let private withheld expected reply =
    let receipt = require expected reply
    let snapshot = receipt.GetProperty("subject").GetProperty("snapshot")

    Expect.equal
        (snapshot.GetProperty("kind").GetString())
        "WITHHELD"
        "Superseded business snapshot requires current full-history permission"

    Expect.equal
        (snapshot.EnumerateObject() |> Seq.map _.Name |> Seq.toList)
        [ "kind" ]
        "Withheld contains only its discriminator and no business payload"

let private summaryHistory fixture reference =
    let history =
        automation
            fixture
            "case.history"
            {|
                caseReference = reference
                detail = "SUMMARY"
                limit = 50
            |}
            0
        |> require "SUCCEEDED"

    Expect.equal
        (history.GetProperty("entries").GetArrayLength())
        2
        "Stale execution and replays add no accepted entries"

    automation
        fixture
        "case.history"
        {|
            caseReference = reference
            detail = "FULL"
            limit = 50
        |}
        2
    |> require "REJECTED"
    |> ignore


let qualify fixture =
    let source, input, reference = original fixture
    let accepted = run fixture fixture.Environment source 0 |> require "ACCEPTED"
    staleReview fixture input reference
    let target = principal fixture "SERVICE" "service"
    setGrant fixture target "CASE_REGISTRAR" true
    setGrant fixture target "CASE_HANDLER" false

    let current =
        automation fixture "case.get" {| caseReference = reference |} 0
        |> require "SUCCEEDED"

    Expect.equal
        (current.GetProperty("revision").GetInt64())
        2L
        "Current snapshot remains disclosed"

    let history =
        current.GetProperty("advice").GetProperty("history").EnumerateArray()
        |> Seq.map (fun detail -> detail.GetString() |> nonNull)
        |> Seq.toList

    Expect.equal history [ "SUMMARY" ] "Revoked full history disappears from scoped advice"

    run fixture fixture.Environment source 0 |> withheld "ALREADY_ACCEPTED"
    automation fixture "operation.preview" input 0 |> withheld "ALREADY_ACCEPTED"
    let operationId = accepted.GetProperty("operationId").GetString() |> nonNull

    automation fixture "operation.observe" {| operationId = operationId |} 0
    |> withheld "SUCCEEDED"

    summaryHistory fixture reference
    setGrant fixture target "CASE_HANDLER" true
    setGrant fixture target "CASE_REGISTRAR" false
