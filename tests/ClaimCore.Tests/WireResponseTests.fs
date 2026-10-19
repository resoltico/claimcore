module ClaimCore.Tests.WireResponseTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Wire
open ClaimCore.TestSupport

let private bytes (raw: string) = Encoding.UTF8.GetBytes raw

let private accepted =
    function
    | Ok value -> value
    | Error problem -> failtestf "Unexpected wire refusal at %s: %s" problem.Path problem.Reason

let private examples () =
    let path =
        Path.Combine(RepositoryRoot.find (), "tests/fixtures/wire-response-examples.json")

    use fixture = JsonDocument.Parse(File.ReadAllBytes(path).AsMemory())
    let mutable seen = Set.empty

    for sample in fixture.RootElement.GetProperty("cases").EnumerateArray() do
        let endpoint =
            Endpoint.tryParse (sample.GetProperty("endpoint").GetString() |> nonNull)
            |> Option.get

        let value =
            Replies.decode endpoint (bytes (sample.GetProperty("value").GetRawText()))
            |> accepted

        Expect.equal (WireReply.endpoint value) endpoint "Reply endpoint is a closed union"

        Expect.equal
            (Replies.decode endpoint (Replies.encode value))
            (Ok value)
            "Every outcome round trips"

        seen <- Set.add endpoint seen

    Expect.equal seen (Set.ofList Endpoint.all) "All twelve response codecs are exercised"

let private malformed () =
    let fault =
        """{"diagnostic":{"id":"CORE_STORE_UNAVAILABLE","parameters":{}},"message":"x","recommendedAction":"STOP_AND_INVESTIGATE"}"""

    Expect.isError
        (Codec.decode 4096 ResponseCodecs.fault (bytes fault))
        "Fault action belongs to its reason"

    for reason in HostFailures.all do
        let encoded = HostFailures.encode reason
        let decoded = HostFailures.decode encoded |> accepted
        Expect.equal decoded.Reason reason "All fixed host policies round trip"
        let node = JsonNode.Parse(encoded) |> nonNull
        node["status"] <- JsonValue.Create(499)

        Expect.isError
            (HostFailures.decode (bytes (node.ToJsonString())))
            "Host status belongs to its reason"

    let ordinary =
        """{"act":"CLOSE","expectedRevision":1,"correction":{"registration":[],"decision":[],"payment":[]}}"""

    let missing = """{"act":"CORRECT_CASE","expectedRevision":1}"""

    for raw in [ ordinary; missing ] do
        Expect.isError
            (Codec.decode 4096 ResponseCodecs.caseActOffer (bytes raw))
            "Correction advice is conditional"

    Expect.isError
        (Codec.decode 1024 ResponseScalars.date (bytes "\"2026-02-30\""))
        "Calendar dates exist"

    Expect.isError
        (Codec.decode 1024 Codec.uuid (bytes "\"10000000-0000-4000-8000-000000000001\\n\""))
        "UUID cannot hide a trailing newline"

let private frames () =
    let raw = """{"endpoint":"case.list","input":{ "limit" : 1 }}"""
    let frame = Frames.admit (bytes raw) |> accepted

    Expect.equal
        (Frames.inputBytes frame)
        (bytes "{ \"limit\" : 1 }")
        "Original admitted input bytes are preserved"

    let metadata =
        {
            OperationId = None
            AttemptKnowledge = AttemptKnowledge.ReadSucceeded
            ResolutionEvidence = ResolutionEvidence.NoEvidence
        }

    let reply =
        WireReply.SessionLogout
            {
                Outcome = SessionLogoutOutcome.Succeeded()
            }

    let result = Frames.fromReply metadata reply
    Expect.equal (Frames.decode (Frames.encode result)) (Ok result) "Typed CLI result round trips"

    for reason in HostFailures.all do
        let failure: HostFailureReply =
            {
                Reason = reason
                Message = HostFailures.render reason
            }

        let value = FrameReply.ServiceFailure(metadata, Endpoint.CaseList, failure)

        Expect.equal
            (Frames.decode (Frames.encode value))
            (Ok value)
            "Typed service failure round trips"

    let local = FrameReply.LocalFailure(metadata, None, LocalFailure.Cancelled)
    Expect.equal (Frames.decode (Frames.encode local)) (Ok local) "Local frame round trips"
    Expect.equal (Frames.exitCode local) 130 "Cancellation exit code"

    let protocol =
        FrameReply.ProtocolFailure(metadata, None, ProtocolFailure.InvalidFrame)

    Expect.equal (Frames.decode (Frames.encode protocol)) (Ok protocol) "Protocol frame round trips"

let private openApi () =
    let first = OpenApi.emit ()
    Expect.equal (OpenApi.emit ()) first "OpenAPI bytes are deterministic"

    Expect.equal
        Fingerprint.value
        (SHA256.HashData first |> Convert.ToHexStringLower)
        "Only OpenAPI bytes are fingerprinted"

    use document = JsonDocument.Parse(first.AsMemory())
    let root = document.RootElement
    Expect.equal (root.GetProperty("openapi").GetString()) "3.1.0" "Explicit dialect"
    let paths = root.GetProperty "paths"
    Expect.equal (paths.EnumerateObject() |> Seq.length) 12 "All endpoints emitted"

    for endpoint in Endpoint.all do
        let operation =
            paths
                .GetProperty(Endpoint.path endpoint)
                .GetProperty((Endpoint.method endpoint).ToLowerInvariant())

        Expect.equal
            (operation.GetProperty("operationId").GetString())
            (Endpoint.id endpoint)
            "Stable endpoint identity"

        Expect.isTrue
            (operation.GetProperty("responses").TryGetProperty("200") |> fst)
            "Successful typed response"

        Expect.isTrue
            (operation.GetProperty("responses").TryGetProperty("500") |> fst)
            "Explicit host errors"

    let schemas = root.GetProperty("components").GetProperty "schemas"

    for name, property, count in [ "Receipt", "scope", 2; "CaseActOffer", "act", 12 ] do
        let union = schemas.GetProperty name
        let discriminator = union.GetProperty "discriminator"

        Expect.equal
            (discriminator.GetProperty("propertyName").GetString())
            property
            "Closed tag identity"

        Expect.equal
            (discriminator.GetProperty("mapping").EnumerateObject() |> Seq.length)
            count
            "All tag tokens map once"

        for branch in union.GetProperty("oneOf").EnumerateArray() do
            Expect.isTrue
                (branch.GetProperty("$ref").GetString()
                 |> nonNull
                 |> _.StartsWith("#/components/schemas/"))
                "Each branch is named"


[<Tests>]
let tests =
    testList
        "typed wire responses"
        [
            testCase "independent examples cover every endpoint and outcome" examples
            testCase "fixed policies and conditional advice reject inconsistent shapes" malformed
            testCase "CLI frames preserve input bytes and round trip typed replies" frames
            testCase "OpenAPI is complete deterministic and presentation free" openApi
        ]
