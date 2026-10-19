module ClaimCore.Tests.WireCorrelationTests

open System
open System.IO
open System.Text.Json
open Expecto
open ClaimCore.Wire
open ClaimCore.TestSupport

let private text name (value: JsonElement) =
    value.GetProperty(name: string).GetString() |> nonNull

let private guid name value = Guid.Parse(text name value)

let private number name (value: JsonElement) =
    value.GetProperty(name: string).GetInt64()

let private optionalGuid name (value: JsonElement) =
    match value.TryGetProperty(name: string) with
    | true, item -> Some(Guid.Parse(item.GetString() |> nonNull))
    | _ -> None

let private request endpoint value =
    match endpoint with
    | Endpoint.CasePreview
    | Endpoint.CaseExecute ->
        RequestIdentity.Case(
            guid "operationId" value,
            text "caseReference" value,
            number "expectedRevision" value,
            text "kind" (value.GetProperty "act")
        )
    | Endpoint.AuthorityExecute ->
        RequestIdentity.Authority(guid "operationId" value, text "kind" (value.GetProperty "act"))
    | Endpoint.AuthorityGrants -> RequestIdentity.Actor(guid "actorId" value)
    | Endpoint.OperationObserve -> RequestIdentity.Operation(guid "operationId" value)
    | _ -> RequestIdentity.Read

let private receipt envelope (data: JsonElement) =
    let scope =
        if text "scope" data = "CASE" then
            ReceiptScope.Case
        else
            ReceiptScope.Authority

    let reference =
        match data.TryGetProperty "caseReference" with
        | true, value -> Some(value.GetString() |> nonNull)
        | _ -> None

    let revision =
        match data.TryGetProperty "revision" with
        | true, value -> Some(value.GetInt64())
        | _ -> None

    ReplyIdentity.Receipt(
        envelope,
        {
            OperationId = guid "operationId" data
            Scope = scope
            Act = text "act" data
            CaseReference = reference
            Revision = revision
        }
    )

let private reply endpoint (value: JsonElement) =
    let envelope = optionalGuid "operationId" value
    let outcome = value.GetProperty "outcome"
    let data = outcome.GetProperty "data"

    match text "tag" outcome with
    | "ACCEPTED"
    | "ALREADY_ACCEPTED" -> receipt envelope data
    | "REVIEWED" ->
        let after = data.GetProperty("review").GetProperty "after"

        ReplyIdentity.Reviewed(
            Option.get envelope,
            text "caseReference" after,
            number "revision" after
        )
    | "OUTCOME_UNKNOWN" -> ReplyIdentity.Unknown(Option.get envelope, guid "operationId" data)
    | "SUCCEEDED" when endpoint = Endpoint.OperationObserve ->
        ReplyIdentity.Observed(guid "operationId" data)
    | "SUCCEEDED" when endpoint = Endpoint.AuthorityGrants ->
        ReplyIdentity.Actor(guid "actorId" (data.GetProperty "actor"))
    | _ -> ReplyIdentity.Other envelope

let private vectors () =
    let path =
        Path.Combine(RepositoryRoot.find (), "tests/fixtures/correlation-vectors.json")

    use fixture = JsonDocument.Parse(File.ReadAllBytes(path).AsMemory())
    let mutable count = 0

    for sample in fixture.RootElement.GetProperty("cases").EnumerateArray() do
        let endpoint = Endpoint.tryParse (text "endpoint" sample) |> Option.get

        let actual =
            Correlation.matchesIdentity
                (request endpoint (sample.GetProperty "request"))
                (reply endpoint (sample.GetProperty "reply"))

        Expect.equal actual (sample.GetProperty("correlated").GetBoolean()) (text "name" sample)
        count <- count + 1

    Expect.equal count 38 "All frozen independent oracle cases were exercised"

[<Tests>]
let tests =
    testList
        "typed wire correlation"
        [ testCase "matches every independent projection vector" vectors ]
