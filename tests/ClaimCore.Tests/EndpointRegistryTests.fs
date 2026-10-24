module ClaimCore.Tests.EndpointRegistryTests

open System
open System.Text
open System.Security.Cryptography
open Expecto
open ClaimCore.Contract

let private encode value = WireWriter.bytes value
let private bytes (value: string) = Encoding.UTF8.GetBytes value

let private accepted =
    Result.defaultWith (fun problem -> failtest (sprintf "Unexpected codec refusal: %A" problem))

let private id = Guid.Parse "10000000-0000-4000-8000-000000000001"

let private roundTrip codec value =
    Expect.equal
        (Codec.encode codec value |> Codec.decode 100000 codec)
        (Ok value)
        "The shared model crosses its codec unchanged."

let private registry () =
    let rows =
        Api.all
        |> List.map (fun endpoint ->
            endpoint.Accept
                { new IEndpointVisitor<_> with
                    member _.Visit value =
                        value.Id, value.Method.Method, value.Effect
                })

    let expected =
        [
            "session"
            "session.logout"
            "context"
            "case.get"
            "case.list"
            "case.history"
            "operation.preview"
            "operation.execute"
            "operation.observe"
            "operation.pending"
            "operation.retry"
            "operation.forget"
            "authority.list"
            "authority.grants"
        ]

    Expect.equal
        (rows |> List.map (fun (id, _, _) -> id) |> List.sort)
        (List.sort expected)
        "The registry contains each adopted endpoint exactly once."

    for endpoint, method, effect in rows do
        Expect.equal
            method
            (if endpoint = "session" then "GET" else "POST")
            "Transport method is explicit."

        Expect.equal
            effect
            (match endpoint with
             | "operation.execute"
             | "operation.retry" -> EndpointEffect.LedgerExecution
             | "operation.forget"
             | "session.logout" -> EndpointEffect.OperationalWrite
             | _ -> EndpointEffect.Read)
            "Operational writes and ledger execution have distinct delivery effects."

let private readReplies () =
    let codec = ReplyCodecs.readReply "BooleanReply" Codec.boolean

    for value in
        [
            ReadReply.Succeeded true
            ReadReply.Succeeded false
            ReadReply.Rejected Refusal.InvalidCursor
            ReadReply.Failed CoreFault.StoreUnavailable
        ] do
        roundTrip codec value

    for input in
        [
            "{\"outcome\":{\"tag\":\"CANCELLED\",\"data\":null}}"
            "{\"outcome\":{\"tag\":\"SUCCEEDED\",\"data\":true,\"message\":\"private\"}}"
        ] do
        Expect.isError
            (Codec.decode 10000 codec (bytes input))
            "Unadvertised outcomes and prose cannot enter an envelope."

let private receipt =
    {
        OperationId = id
        Position =
            {
                Sequence = 1L
                EntryHash = String.replicate 64 "a"
            }
        RecordedAt = DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero)
        RecordedBy = id
        Scope =
            ReceiptScope.Authority
                {
                    TargetActorId = id
                    Act = AuthorityKind.RegisterActor
                }
    }

let private authorityReview =
    OperationReview.Authority
        {
            Target = None
            Before = { Enabled = false; Grants = [] }
            After = { Enabled = true; Grants = [] }
        }

let private mutationReplies () =
    for outcome in
        [
            PreviewResult.Reviewed authorityReview
            PreviewResult.AlreadyAccepted receipt
            PreviewResult.Rejected Refusal.LastOwner
            PreviewResult.Failed CoreFault.StoreIntegrityError
        ] do
        roundTrip
            ReplyCodecs.preview
            {
                PreviewReply.OperationId = id
                Outcome = outcome
            }

    for outcome in
        [
            ExecuteResult.Accepted receipt
            ExecuteResult.AlreadyAccepted receipt
            ExecuteResult.Rejected Refusal.IdempotencyConflict
            ExecuteResult.Failed CoreFault.StoreUnavailable
            ExecuteResult.OutcomeUnknown
        ] do
        roundTrip
            ReplyCodecs.execute
            {
                RecoveryId = None
                ExecuteReply.OperationId = id
                Outcome = outcome
            }

    let parsed =
        Codec.encode ReceiptCodecs.receipt receipt |> StrictJson.read 10000 |> accepted

    let scope = JsonValue.tryMember "scope" parsed |> Option.get

    Expect.equal
        (JsonValue.tryMember "kind" scope)
        (Some(JsonValue.String "AUTHORITY"))
        "Receipt scope owns its target fields."

    Expect.equal
        (JsonValue.tryMember "targetActorId" parsed)
        None
        "The target is nested under its scope."

let private structuralIdentity () =
    let document = OpenApi.document

    let expected =
        document |> CanonicalWriter.bytes |> SHA256.HashData |> Convert.ToHexStringLower

    Expect.equal
        ContractIdentity.value
        expected
        "The in-process identity is the canonical structural document hash."

    let text = encode document |> Encoding.UTF8.GetString

    let schemas =
        JsonValue.tryMember "components" document
        |> Option.bind (JsonValue.tryMember "schemas")
        |> Option.get

    let schemaText = encode schemas |> Encoding.UTF8.GetString

    Expect.isFalse
        (schemaText.Contains("\"description\"", StringComparison.Ordinal))
        "Shared schemas contain no prose."

    let parsed = Microsoft.OpenApi.OpenApiDocument.Parse(text, "json")

    Expect.isEmpty
        (nonNull parsed.Diagnostic).Errors
        "The independent OpenAPI parser admits the registry document."

    for omitted in [ "\"message\""; "\"CANCELLED\""; "\"case.execute\""; "\"authority.execute\"" ] do
        Expect.isFalse
            (text.Contains(omitted, StringComparison.Ordinal))
            "The structural registry document excludes retired protocol surface."

let private hostFailures () =
    for reason in ClaimCore.Contract.Protocol.HostFailures.all do
        let expected = HostFailureCodecs.ofReason reason

        Expect.equal
            (HostFailureCodecs.encode reason |> HostFailureCodecs.decode)
            (Ok expected)
            "The minimal host payload round trips."

    let original = HostFailureCodecs.ofReason HostFailureReason.CompletedResponseFailed
    Expect.equal original.Phase None "The actual baseline's completed-response phase is preserved."

    let injected =
        JsonValue.Object
            [
                "hostFailure",
                JsonValue.Object
                    [
                        "code", JsonValue.String "INPUT_INVALID"
                        "phase", JsonValue.String "NOT_STARTED"
                        "message", JsonValue.String "private"
                    ]
            ]

    Expect.isError
        (injected |> encode |> HostFailureCodecs.decode)
        "Host failures refuse server prose."

[<Tests>]
let tests =
    testList
        "typed endpoint registry"
        [
            testCase "the eleven endpoints own method and mutation classification" registry
            testCase "one read envelope carries success, refusal and fault" readReplies
            testCase "preview and execution share receipt and refusal models" mutationReplies
            testCase "identity includes only the registry's structural OpenAPI" structuralIdentity
            testCase "host failures contain only code and phase" hostFailures
        ]
