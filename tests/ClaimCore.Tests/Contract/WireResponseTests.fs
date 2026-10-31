module ClaimCore.Tests.WireResponseTests

open ClaimCore.Cli

open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open ClaimCore.TestSupport

let private bytes (raw: string) = Encoding.UTF8.GetBytes raw

let private accepted =
    function
    | Ok value -> value
    | Error problem -> failtestf "Unexpected wire refusal at %s: %s" problem.Path problem.Reason

let private expectedExit =
    function
    | "SUCCEEDED"
    | "REVIEWED"
    | "ACCEPTED"
    | "ALREADY_ACCEPTED" -> 0
    | "REJECTED"
    | "NOT_ACCEPTED" -> 2
    | "FAILED" -> 3
    | "OUTCOME_UNKNOWN" -> 4
    | _ -> failtest "The authored reply requires a declared outcome kind."

let private examples () =
    for definition in Api.definitions do
        let work =
            definition.Accept
                { new IEndpointVisitor<System.Action> with
                    member _.Visit<'Input, 'Output>(endpoint: Endpoint<'Input, 'Output>) =
                        System.Action(fun () -> ResponseExamples.defaultReply endpoint |> ignore)
                }

        work.Invoke()

    let path =
        Path.Combine(RepositoryRoot.find (), "tests/fixtures/wire-response-examples.json")

    use fixture = JsonDocument.Parse(File.ReadAllBytes(path).AsMemory())
    let mutable seen = Set.empty

    for sample in fixture.RootElement.GetProperty("cases").EnumerateArray() do
        let endpoint =
            Api.tryFind (sample.GetProperty("endpoint").GetString() |> nonNull)
            |> Option.get

        let raw = bytes (sample.GetProperty("value").GetRawText())
        let value = StrictJson.read Limits.MaximumResponseBytes raw |> accepted
        let expected = sample.GetProperty("valid").GetBoolean()

        let encoded = endpoint.NormalizeOutput value

        Expect.equal
            (Result.isOk encoded)
            expected
            (sample.GetProperty("name").GetString() |> nonNull)

        if expected then
            let classification = endpoint.ClassifyOutput value |> accepted

            let kind =
                sample.GetProperty("value").GetProperty("outcome").GetProperty("kind").GetString()
                |> nonNull

            Expect.equal
                (ReplyKind.token classification.Kind)
                kind
                "Endpoint-owned classification agrees with the independent wire outcome"

            Expect.equal
                (ExitCode.value classification.ExitCode)
                (expectedExit kind)
                "Every wire outcome retains its declared exit meaning"

            Expect.equal
                (CanonicalWriter.bytes (encoded |> accepted))
                (CanonicalWriter.bytes value)
                "Typed replies preserve admitted values."

        seen <- Set.add (Api.id endpoint) seen

    Expect.equal
        seen
        (Api.all |> List.map Api.id |> Set.ofList)
        "All registry response codecs are exercised."

let private malformed () =
    let fault = """{"code":"CORE_STORE_UNAVAILABLE","action":"STOP_AND_INVESTIGATE"}"""

    Expect.isOk
        (Codec.decode
            4096
            DiagnosticCodecs.fault
            (bytes (fault.Replace("STOP_AND_INVESTIGATE", "RETRY_SAFE"))))
        "Changing only the action admits the fault."

    Expect.isError
        (Codec.decode 4096 DiagnosticCodecs.fault (bytes fault))
        "Fault action belongs to its reason"

    for reason in HostFailureReason.all do
        let encoded = HostFailureCodecs.encode reason
        let decoded = HostFailureCodecs.decode encoded |> accepted

        Expect.equal
            decoded
            (HostFailureCodecs.ofReason reason)
            "All fixed host policies round trip"

        let node = JsonNode.Parse(encoded) |> nonNull
        (node["hostFailure"] |> nonNull)["phase"] <- JsonValue.Create("UNRECOGNIZED")

        Expect.isError
            (HostFailureCodecs.decode (bytes (node.ToJsonString())))
            "Host phases form a closed vocabulary"

    let ordinary =
        """{"acts":["CLOSE"],"correction":{"registration":["KEEP"],"decision":["KEEP"],"payment":["KEEP"]},"history":[]}"""

    let missing = """{"acts":["CORRECT"],"history":[]}"""

    for raw in [ ordinary; missing ] do
        Expect.isError
            (Codec.decode 4096 ResponseCodecs.caseAdvice (bytes raw))
            "Correction advice is conditional"

    Expect.isError
        (Codec.decode 1024 ResponseScalars.date (bytes "\"2026-02-30\""))
        "Calendar dates exist"

    Expect.isError
        (Codec.decode 1024 Codec.uuid (bytes "\"10000000-0000-4000-8000-000000000001\\n\""))
        "UUID cannot hide a trailing newline"

let private frames () =
    let raw = """{"endpoint":"case.list","input":{ "limit" : 1 }}"""
    let frame = ClaimCore.Cli.Frames.admit (bytes raw) |> accepted

    Expect.equal
        (ClaimCore.Cli.Frames.inputBytes frame)
        (bytes """{"limit":1}""")
        "The admitted body is compacted once before freezing."

    let reply: ClaimCore.Cli.FrameReply =
        {
            Endpoint = Some(EndpointId.Host HostEndpoint.Logout)
            OperationId = None
            Delivery = DeliveryState.Delivered
            Result =
                FrameResult.Reply(
                    ClaimCore.Tests.CliReplies.ofValue Api.logout (Outcome.Succeeded())
                )
        }

    let roundTrip value =
        Expect.equal
            (ClaimCore.Cli.Frames.decode (ClaimCore.Cli.Frames.encode value)
             |> Result.map CliReplies.frameFacts)
            (Ok(CliReplies.frameFacts value))
            "CLI result frames round trip."

    roundTrip reply

    for reason in HostFailureReason.all do
        roundTrip
            { reply with
                Result = FrameResult.HostFailure(HostFailureCodecs.ofReason reason)
            }

    let local =
        { reply with
            Delivery = DeliveryState.NotSent
            Result = FrameResult.LocalFailure CliFailure.Cancelled
        }

    roundTrip local
    Expect.equal (ClaimCore.Cli.Frames.exitCode local) 130 "Cancellation exit code"

let private checkDiscriminators (root: JsonElement) =
    let schemas = root.GetProperty("components").GetProperty "schemas"

    for name, property, count in [ "ReceiptSubject", "kind", 2 ] do
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

let private openApi () =
    let first = (ClaimCore.Protocol.OpenApi.document |> WireWriter.bytes)

    Expect.equal
        ((ClaimCore.Protocol.OpenApi.document |> WireWriter.bytes))
        first
        "OpenAPI bytes are deterministic"

    use document = JsonDocument.Parse(first.AsMemory())
    let root = document.RootElement
    Expect.equal (root.GetProperty("openapi").GetString()) "3.1.0" "Explicit dialect"
    let paths = root.GetProperty "paths"

    Expect.equal
        (paths.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq)
        (Api.all |> List.map Api.path |> Set.ofList)
        "Exactly the current registry paths are emitted"

    for endpoint in Api.all do
        let operation =
            paths
                .GetProperty(Api.path endpoint)
                .GetProperty(((Api.method endpoint).Method).ToLowerInvariant())

        Expect.equal
            (operation.GetProperty("operationId").GetString())
            (Api.id endpoint)
            "Stable endpoint identity"

        Expect.equal
            (operation.GetProperty("x-claimcore-effect").GetString())
            (EndpointEffect.token (Api.effect endpoint))
            "Delivery effect is part of the hashed public contract"

        Expect.isTrue
            (operation.GetProperty("responses").TryGetProperty("200") |> fst)
            "Successful typed response"

        Expect.isTrue
            (operation.GetProperty("responses").TryGetProperty("500") |> fst)
            "Explicit host errors"

    checkDiscriminators root


[<Tests>]
let tests =
    testList
        "typed wire responses"
        [
            testCase "independent examples cover every endpoint and outcome" examples
            testCase "fixed policies and conditional advice reject inconsistent shapes" malformed
            testCase "CLI frames freeze prepared input bytes and round trip typed replies" frames
            testCase "OpenAPI is complete deterministic and presentation free" openApi
        ]
