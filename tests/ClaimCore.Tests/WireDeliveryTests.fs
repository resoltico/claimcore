module ClaimCore.Tests.WireDeliveryTests

open System
open System.IO
open System.Text.Json
open Expecto
open ClaimCore.Wire
open ClaimCore.TestSupport

let private text name (value: JsonElement) =
    value.GetProperty(name: string).GetString() |> nonNull

let private named options token =
    options |> List.find (snd >> (=) token) |> fst

let private knowledges =
    [
        AttemptKnowledge.NotSent, "NOT_SENT"
        AttemptKnowledge.ReadSucceeded, "READ_SUCCEEDED"
        AttemptKnowledge.ReadFailed, "READ_FAILED"
        AttemptKnowledge.Accepted, "ACCEPTED"
        AttemptKnowledge.NotAccepted, "NOT_ACCEPTED"
        AttemptKnowledge.Unconfirmed, "UNCONFIRMED"
    ]

let private evidences =
    [
        ResolutionEvidence.NoEvidence, "NONE"
        ResolutionEvidence.ContentAccepted, "CONTENT_ACCEPTED"
        ResolutionEvidence.ContentExcluded, "CONTENT_EXCLUDED"
    ]

let private tags =
    [
        OutcomeTag.Succeeded, "SUCCEEDED"
        OutcomeTag.Reviewed, "REVIEWED"
        OutcomeTag.Accepted, "ACCEPTED"
        OutcomeTag.AlreadyAccepted, "ALREADY_ACCEPTED"
        OutcomeTag.Rejected, "REJECTED"
        OutcomeTag.Failed, "FAILED"
        OutcomeTag.Cancelled, "CANCELLED"
        OutcomeTag.OutcomeUnknown, "OUTCOME_UNKNOWN"
    ]

let private optional name (value: JsonElement) =
    match value.TryGetProperty(name: string) with
    | true, property when property.ValueKind = JsonValueKind.String ->
        Some(property.GetString() |> nonNull)
    | _ -> None

let private attempt value =
    match text "kind" value with
    | "NOT_DISPATCHED" -> AttemptResult.NotDispatched
    | "DISPATCHED_WITHOUT_REPLY" -> AttemptResult.DispatchedWithoutReply
    | "HOST_FAILURE" ->
        let phase =
            optional "phase" value
            |> Option.map (
                named
                    [
                        ExecutionPhase.NotStarted, "NOT_STARTED"
                        ExecutionPhase.StartedUnconfirmed, "STARTED_UNCONFIRMED"
                    ]
            )

        AttemptResult.HostFailure phase
    | "OUTCOME" -> AttemptResult.Outcome(named tags (text "tag" value), optional "diagnostic" value)
    | _ -> failtest "Unknown independent attempt token"

let private accumulated =
    function
    | "CONTENT_EXCLUDED" -> AccumulatedKnowledge.ContentExcluded
    | token -> AccumulatedKnowledge.Known(named knowledges token)

let private vectors (group: string) count action =
    use document =
        JsonDocument.Parse(
            File
                .ReadAllBytes(
                    Path.Combine(RepositoryRoot.find (), "tests/fixtures/delivery-vectors.json")
                )
                .AsMemory()
        )

    let values = document.RootElement.GetProperty group
    Expect.equal (values.GetArrayLength()) count "Complete independent vector inventory"

    for value in values.EnumerateArray() do
        action value

let private knowledge () =
    vectors "knowledge" 127 (fun value ->
        let endpoint = Endpoint.tryParse (text "endpoint" value) |> Option.get

        Expect.equal
            (Delivery.knowledge endpoint (attempt (value.GetProperty "attempt")))
            (named knowledges (text "knowledge" value))
            "Independent knowledge oracle")

let private evidence () =
    vectors "evidence" 127 (fun value ->
        let endpoint = Endpoint.tryParse (text "endpoint" value) |> Option.get

        Expect.equal
            (Delivery.evidence endpoint (attempt (value.GetProperty "attempt")))
            (named evidences (text "evidence" value))
            "Independent content-evidence oracle")

let private accumulation () =
    vectors "accumulate" 126 (fun value ->
        let actual =
            Delivery.accumulate
                (accumulated (text "prior" value))
                (named knowledges (text "attempt" value))
                (named evidences (text "evidence" value))

        Expect.equal
            actual
            (accumulated (text "result" value))
            "Independent accumulated-knowledge oracle")

[<Tests>]
let tests =
    testList
        "typed delivery policy"
        [
            testCase "all knowledge vectors" knowledge
            testCase "all content evidence vectors" evidence
            testCase "all accumulation vectors" accumulation
        ]
