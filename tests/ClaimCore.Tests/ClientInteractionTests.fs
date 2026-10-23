module ClaimCore.Tests.ClientInteractionTests

open ClaimCore.Contract

open System
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Contract.Protocol
open CliTransportFixture
open ClaimCore.Cli

let private outcomeKnowledge endpoint =
    let mutates = Endpoint.mutates endpoint

    for tag in
        [
            OutcomeTag.Succeeded
            OutcomeTag.Reviewed
            OutcomeTag.Accepted
            OutcomeTag.AlreadyAccepted
            OutcomeTag.Rejected
            OutcomeTag.Failed
            OutcomeTag.Cancelled
            OutcomeTag.OutcomeUnknown
        ] do
        let expected =
            match tag with
            | OutcomeTag.Succeeded
            | OutcomeTag.Reviewed -> AttemptKnowledge.ReadSucceeded
            | OutcomeTag.Accepted
            | OutcomeTag.AlreadyAccepted -> AttemptKnowledge.Accepted
            | OutcomeTag.OutcomeUnknown -> AttemptKnowledge.Unconfirmed
            | _ ->
                if mutates then
                    AttemptKnowledge.NotAccepted
                else
                    AttemptKnowledge.ReadFailed

        Expect.equal
            (Delivery.knowledge endpoint (AttemptResult.Outcome(tag, None)))
            expected
            "All typed result tags classify."


let private policyTruthTable () =
    for endpoint in [ Endpoint.CaseGet; Endpoint.CaseExecute ] do
        let mutates = Endpoint.mutates endpoint

        Expect.equal
            (Delivery.knowledge endpoint AttemptResult.NotDispatched)
            AttemptKnowledge.NotSent
            "No dispatch means no delivery."

        Expect.equal
            (Delivery.knowledge endpoint AttemptResult.DispatchedWithoutReply)
            (if mutates then
                 AttemptKnowledge.Unconfirmed
             else
                 AttemptKnowledge.ReadFailed)
            "Lost reply follows endpoint effect."

        for phase in
            [ None; Some ExecutionPhase.NotStarted; Some ExecutionPhase.StartedUnconfirmed ] do
            let expected =
                if not mutates then
                    AttemptKnowledge.ReadFailed
                elif phase = Some ExecutionPhase.NotStarted then
                    AttemptKnowledge.NotAccepted
                else
                    AttemptKnowledge.Unconfirmed

            Expect.equal
                (Delivery.knowledge endpoint (AttemptResult.HostFailure phase))
                expected
                "Host phase bounds uncertainty."

        outcomeKnowledge endpoint

    Expect.isError
        (Replies.decode
            Endpoint.Context
            (bytes "{\"endpoint\":\"context\",\"outcome\":{\"tag\":\"SYNTHETIC\",\"data\":{}}}"))
        "Unknown wire tags refuse."

let private accumulatedEvidence () =
    let conflict = "OPERATION_CONTENT_CONFLICT"

    Expect.equal
        (Delivery.evidence
            Endpoint.CaseExecute
            (AttemptResult.Outcome(OutcomeTag.Rejected, Some conflict)))
        ResolutionEvidence.ContentExcluded
        "Only immutable operation-content conflict excludes content."

    for attempt in
        [
            AttemptKnowledge.NotSent
            AttemptKnowledge.NotAccepted
            AttemptKnowledge.ReadFailed
            AttemptKnowledge.ReadSucceeded
        ] do
        Expect.equal
            (Delivery.accumulate
                (AccumulatedKnowledge.Known AttemptKnowledge.Unconfirmed)
                attempt
                ResolutionEvidence.NoEvidence)
            (AccumulatedKnowledge.Known AttemptKnowledge.Unconfirmed)
            "Another attempt alone cannot erase uncertainty."

    Expect.equal
        (Delivery.accumulate
            (AccumulatedKnowledge.Known AttemptKnowledge.Unconfirmed)
            AttemptKnowledge.Accepted
            ResolutionEvidence.ContentAccepted)
        (AccumulatedKnowledge.Known AttemptKnowledge.Accepted)
        "Exact replay proves acceptance."

    Expect.equal
        (Delivery.accumulate
            (AccumulatedKnowledge.Known AttemptKnowledge.Unconfirmed)
            AttemptKnowledge.NotAccepted
            ResolutionEvidence.ContentExcluded)
        AccumulatedKnowledge.ContentExcluded
        "Conflict excludes different content."

let private integerLexemes () =
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
        Expect.equal
            (Codec.decode 100 (Codec.integer 0L JsonValue.MaximumInteger) (bytes spelling)
             |> Result.isOk)
            accepted
            "Integer lexical profile."

let private responseFixture endpoint tag =
    samples "wire-response-examples.json" endpoint
    |> List.find (fun bytes ->
        use document = JsonDocument.Parse(ReadOnlyMemory bytes)
        document.RootElement.GetProperty("outcome").GetProperty("tag").GetString() = tag)
    |> fun bytes -> JsonNode.Parse(bytes) |> nonNull

let private receiptCorrelation () =
    let invocation = CliTransportFixture.frame "case.execute"
    let original = responseFixture "case.execute" "ACCEPTED"

    let bytes (node: JsonNode) =
        Encoding.UTF8.GetBytes(node.ToJsonString())

    Expect.isOk (CliTransportFixture.result invocation (bytes original)) "Matching acceptance"

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
            (CliTransportFixture.result invocation (bytes changed))
            "Wrong receipt identity cannot confirm"

    let wrongEnvelope = original.DeepClone()
    wrongEnvelope["operationId"] <- JsonValue.Create("10000000-0000-4000-8000-000000000002")

    Expect.isError
        (CliTransportFixture.result invocation (bytes wrongEnvelope))
        "Echoed request ID binds every outcome"

let private previewCorrelation () =
    let invocation = CliTransportFixture.frame "case.preview"
    let original = responseFixture "case.preview" "REVIEWED"

    let bytes (node: JsonNode) =
        Encoding.UTF8.GetBytes(node.ToJsonString())

    Expect.isOk (CliTransportFixture.result invocation (bytes original)) "Matching preview"

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
            (CliTransportFixture.result invocation (bytes changed))
            "Preview target and proposed revision bind request"

    let replay = responseFixture "case.preview" "ALREADY_ACCEPTED"

    let accepted =
        CliTransportFixture.result invocation (bytes replay)
        |> Fixtures.accepted
        |> CliReply.result invocation
        |> fun reply -> Frames.decode reply.Bytes |> Fixtures.accepted

    Expect.equal
        (Frames.metadata accepted).AttemptKnowledge
        AttemptKnowledge.Accepted
        "Exact preview replay confirms content."

    Expect.equal
        (Frames.metadata accepted).ResolutionEvidence
        ResolutionEvidence.ContentAccepted
        "Acceptance is explicit."

let private equivalentReference () =
    let invocation = CliTransportFixture.frame "case.execute"
    let receipt = responseFixture "case.execute" "ACCEPTED"
    let data = receipt["outcome"] |> nonNull |> fun node -> node["data"] |> nonNull
    data["caseReference"] <- JsonValue.Create("X")
    let response = receipt.ToJsonString() |> Encoding.UTF8.GetBytes

    Expect.isOk
        (CliTransportFixture.result invocation response)
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

    Expect.isOk
        (CliTransportFixture.result invocation (encoded original))
        "Matching target read succeeds"

    let changed = original.DeepClone()

    let actor =
        changed["outcome"]
        |> nonNull
        |> fun node -> node["data"] |> nonNull |> fun node -> node["actor"] |> nonNull

    actor["actorId"] <- JsonValue.Create("10000000-0000-4000-8000-000000000002")

    Expect.isOk
        (Replies.decode Endpoint.AuthorityGrants (encoded changed))
        "Wrong target is independently schema-valid"

    Expect.isError
        (CliTransportFixture.result invocation (encoded changed))
        "Read target correlation rejects another actor"

    Expect.equal
        (Delivery.knowledge Endpoint.AuthorityGrants AttemptResult.DispatchedWithoutReply)
        AttemptKnowledge.ReadFailed
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
