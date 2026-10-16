module ClaimCore.Tests.ClientProtocolTests

open System
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open ClaimCore.ClientProtocol
open ClaimCore.Contracts
open ClaimCore.ClientContract

let private policyTruthTable () =
    for dispatched, mutates, expected in
        [
            false, false, "NOT_SENT"
            false, true, "NOT_SENT"
            true, false, "READ_FAILED"
            true, true, "UNCONFIRMED"
        ] do
        Expect.equal (DeliveryPolicy.missing dispatched mutates) expected "Transport knowledge"

    for mutates, phase, expected in
        [
            false, None, "READ_FAILED"
            false, Some "NOT_STARTED", "READ_FAILED"
            true, None, "UNCONFIRMED"
            true, Some "NOT_STARTED", "NOT_ACCEPTED"
            true, Some "STARTED_UNCONFIRMED", "UNCONFIRMED"
        ] do
        Expect.equal (DeliveryPolicy.host mutates phase) expected "Host knowledge"

    for mutates, tag, expected in
        [
            false, "SUCCEEDED", "READ_SUCCEEDED"
            false, "REVIEWED", "READ_SUCCEEDED"
            false, "ALREADY_ACCEPTED", "ACCEPTED"
            true, "ACCEPTED", "ACCEPTED"
            true, "ALREADY_ACCEPTED", "ACCEPTED"
            true, "REJECTED", "NOT_ACCEPTED"
            true, "FAILED", "NOT_ACCEPTED"
            true, "CANCELLED", "NOT_ACCEPTED"
            false, "REJECTED", "READ_FAILED"
            false, "FAILED", "READ_FAILED"
            false, "CANCELLED", "READ_FAILED"
            true, "OUTCOME_UNKNOWN", "UNCONFIRMED"
        ] do
        Expect.equal (DeliveryPolicy.outcome mutates tag) expected "Typed result knowledge"

    Expect.throws
        (fun () -> DeliveryPolicy.outcome true "SYNTHETIC" |> ignore)
        "Uncovered outcomes refuse"

let private accumulatedEvidence () =
    Expect.equal
        DeliveryPolicy.contentExcludedDiagnostic
        (ClaimCore.Application.RejectionDiagnosticIds.token
            ClaimCore.Application.RejectionDiagnosticId.IdempotencyConflict)
        "Only the authoritative immutable-ID conflict diagnostic proves content exclusion"

    for attempt in [ "NOT_SENT"; "NOT_ACCEPTED"; "READ_FAILED"; "READ_SUCCEEDED" ] do
        Expect.equal
            (DeliveryPolicy.accumulate "UNCONFIRMED" attempt "NONE")
            "UNCONFIRMED"
            "A later attempt cannot erase earlier uncertainty"

    Expect.equal
        (DeliveryPolicy.accumulate "UNCONFIRMED" "ACCEPTED" "CONTENT_ACCEPTED")
        "ACCEPTED"
        "Exact replay proves acceptance"

    Expect.equal
        (DeliveryPolicy.accumulate "UNCONFIRMED" "NOT_ACCEPTED" "CONTENT_EXCLUDED")
        "CONTENT_EXCLUDED"
        "Conflict proves exclusion, not acceptance"

let private integerLexemes () =
    let schema =
        {
            Identifier = "test"
            Title = "test"
            Root = Schema.integer (Some 0L) (Some 9007199254740991L)
            Definitions = []
        }

    for spelling, accepted in
        [
            "0", true
            "-0", true
            "1", true
            "1.0", false
            "1e0", false
            "9007199254740991", true
            "9007199254740992", false
            "-1", false
        ] do
        let actual =
            HttpInputSupport.parse
                (Encoding.UTF8.GetBytes spelling)
                (SchemaValueValidation.verify schema)
            |> Result.defaultValue false

        Expect.equal actual accepted "Integer lexical profile"

let private responseFixture endpoint tag =
    let definition = ClaimCore.Contracts.EndpointCatalog.tryFind endpoint |> Option.get

    SchemaSamples.variants definition.Response
    |> List.find (fun bytes ->
        use document = JsonDocument.Parse(ReadOnlyMemory bytes)
        document.RootElement.GetProperty("outcome").GetProperty("tag").GetString() = tag)
    |> fun bytes -> JsonNode.Parse(bytes) |> nonNull

let private receiptCorrelation () =
    let invocation = CliTransportFixture.frame "case.execute"
    let original = responseFixture "case.execute" "ACCEPTED"

    let bytes (node: JsonNode) =
        Encoding.UTF8.GetBytes(node.ToJsonString())

    Expect.isOk (Frames.result invocation (bytes original)) "Matching acceptance"

    let text (value: string) : JsonNode =
        (JsonValue.Create(value) |> nonNull) :> JsonNode

    for field, value in
        [
            "operationId", text "10000000-0000-4000-8000-000000000002"
            "caseReference", text "another"
            "act", text "CLOSE"
            "revision", JsonValue.Create(2) :> JsonNode
        ] do
        let changed = original.DeepClone()
        let receipt = changed["outcome"] |> nonNull |> fun node -> node["data"] |> nonNull
        receipt[field] <- value

        Expect.isError
            (Frames.result invocation (bytes changed))
            "Wrong receipt identity cannot confirm"

    let wrongEnvelope = original.DeepClone()
    wrongEnvelope["operationId"] <- JsonValue.Create("10000000-0000-4000-8000-000000000002")

    Expect.isError
        (Frames.result invocation (bytes wrongEnvelope))
        "Echoed request ID binds every outcome"

let private previewCorrelation () =
    let invocation = CliTransportFixture.frame "case.preview"
    let original = responseFixture "case.preview" "REVIEWED"

    let bytes (node: JsonNode) =
        Encoding.UTF8.GetBytes(node.ToJsonString())

    Expect.isOk (Frames.result invocation (bytes original)) "Matching preview"

    for field, value in [ "caseReference", "another"; "revision", "2" ] do
        let changed = original.DeepClone()

        let after =
            changed["outcome"]
            |> nonNull
            |> fun node ->
                node["data"]
                |> nonNull
                |> fun node -> node["review"] |> nonNull |> fun node -> node["after"] |> nonNull

        after[field] <-
            if field = "revision" then
                JsonValue.Create(2) :> JsonNode
            else
                (JsonValue.Create(value) |> nonNull) :> JsonNode

        Expect.isError
            (Frames.result invocation (bytes changed))
            "Preview target and proposed revision bind request"

    let replay = responseFixture "case.preview" "ALREADY_ACCEPTED"

    let accepted =
        Frames.result invocation (bytes replay)
        |> Fixtures.accepted
        |> Frames.decode
        |> Fixtures.accepted

    Expect.equal
        (accepted.GetProperty("attemptKnowledge").GetString())
        "ACCEPTED"
        "Exact preview replay confirms content"

    Expect.equal
        (accepted.GetProperty("resolutionEvidence").GetString())
        "CONTENT_ACCEPTED"
        "Acceptance is explicit"

let private equivalentReference () =
    let invocation = CliTransportFixture.frame "case.execute"
    let receipt = responseFixture "case.execute" "ACCEPTED"
    let data = receipt["outcome"] |> nonNull |> fun node -> node["data"] |> nonNull
    data["caseReference"] <- JsonValue.Create("X")
    let response = receipt.ToJsonString() |> Encoding.UTF8.GetBytes

    Expect.isOk
        (Frames.result invocation response)
        "ASCII reference identity accepts retained case spelling"

    Expect.equal
        (Frames.inputBytes invocation)
        (Frames.inputBytes (CliTransportFixture.frame "case.execute"))
        "Correlation leaves exact authored bytes unchanged"

let private actorQueryCorrelation () =
    let invocation = CliTransportFixture.frame "authority.grants"
    let original = responseFixture "authority.grants" "SUCCEEDED"

    let encoded (node: JsonNode) =
        Encoding.UTF8.GetBytes(node.ToJsonString())

    Expect.isOk (Frames.result invocation (encoded original)) "Matching target read succeeds"
    let changed = original.DeepClone()

    let actor =
        changed["outcome"]
        |> nonNull
        |> fun node -> node["data"] |> nonNull |> fun node -> node["actor"] |> nonNull

    actor["actorId"] <- JsonValue.Create("10000000-0000-4000-8000-000000000002")

    Expect.isOk
        (EndpointCatalog.admitResponse "authority.grants" (encoded changed))
        "Wrong target is independently schema-valid"

    Expect.isError
        (Frames.result invocation (encoded changed))
        "Read target correlation rejects another actor"

    Expect.equal
        (DeliveryPolicy.missing true false)
        "READ_FAILED"
        "Target read loss is never mutation uncertainty"

[<Tests>]
let tests =
    testList
        "Client protocol"
        [
            testCase "actor query replies bind the exact requested target" actorQueryCorrelation
            testCase "independent finite delivery truth table" policyTruthTable
            testCase "accumulated attempts require exact evidence" accumulatedEvidence
            testCase "integer admission retains lexical spelling" integerLexemes
            testCase "receipt and envelope correlation reject wrong identities" receiptCorrelation
            testCase "preview review and replay require exact correlation" previewCorrelation
            testCase "reference case identity preserves authored request bytes" equivalentReference
        ]
