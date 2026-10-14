module ClaimCore.Tests.ContractTests

open System
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private bytes (text: string) = Encoding.UTF8.GetBytes text
let private id = Guid.Parse "20000000-0000-4000-8000-000000000001"
let private digest = String.replicate 64 "a"
let private position = { Sequence = 1L; EntryHash = digest }

let private receipt =
    {
        OperationId = id
        Position = position
        CaseReference = "UNIT-001"
        Revision = 1L
        Act = AcceptedAct.Command CommandKind.Open
        Disposition = Some CaseDisposition.Active
        Snapshot = Some(Claim.view (opened ()))
        RecordedAt = DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero)
        RecordedBy = id
    }

let private encode endpoint value = OutcomeCodec.encode endpoint value

let private admitted endpoint value =
    encode endpoint value |> EndpointCatalog.admitResponse endpoint |> accepted

let private mutate bytes key value =
    let node = JsonNode.Parse(bytes: byte array) |> nonNull
    node.AsObject().Add(key, JsonValue.Create<string> value)
    node.ToJsonString() |> Encoding.UTF8.GetBytes

let private exactCatalogue () =
    let expected =
        [
            "session"
            "session.logout"
            "definition"
            "case.get"
            "case.list"
            "case.history"
            "operation.observe"
            "command.preview"
            "command.execute"
            "case.steward"
            "authority.register"
            "authority.setGrant"
            "authority.setEnabled"
            "authority.observe"
        ]

    Expect.equal
        (EndpointCatalog.all |> List.map _.Identifier)
        expected
        "One exact endpoint inventory"

    for endpoint in EndpointCatalog.all do
        Expect.equal endpoint.Path ("/api/" + endpoint.Identifier) "Literal path"

        Expect.equal
            endpoint.Method
            (if endpoint.Identifier = "session" then "GET" else "POST")
            "Method"

    Expect.equal
        (EndpointCatalog.all |> List.filter _.Mutates |> List.map _.Identifier)
        [
            "command.execute"
            "case.steward"
            "authority.register"
            "authority.setGrant"
            "authority.setEnabled"
        ]
        "All durable mutations"

let private requestAlternatives () =
    let mutable count = 0

    for endpoint in EndpointCatalog.all do
        for sample in SchemaSamples.variants endpoint.Request do
            EndpointCatalog.admitRequest endpoint.Identifier sample |> accepted |> ignore
            let frame = JsonObject()
            frame.Add("endpoint", JsonValue.Create endpoint.Identifier)
            frame.Add("input", JsonNode.Parse sample)
            let parsed = Frames.parse (bytes (frame.ToJsonString())) |> accepted
            Expect.equal (Frames.inputBytes parsed) sample "Exact input bytes preserved"

            Expect.isError
                (EndpointCatalog.admitRequest endpoint.Identifier (mutate sample "unexpected" "x"))
                "Unknown member refused"

            count <- count + 1

    Expect.equal count 32 "Every command and stewardship branch reached"

let private responseAlternatives () =
    let mutable count = 0

    for endpoint in EndpointCatalog.all do
        for sample in SchemaSamples.variants endpoint.Response do
            EndpointCatalog.admitResponse endpoint.Identifier sample |> accepted |> ignore
            let frame = Frames.result endpoint.Identifier sample |> accepted
            let decoded = Frames.decode frame |> accepted

            Expect.equal
                (decoded.GetProperty("endpoint").GetString())
                endpoint.Identifier
                "Round-trip identity"

            Expect.isError
                (Frames.decode (mutate frame "unexpected" "x"))
                "Extra frame member refused"

            count <- count + 1

    Expect.equal count 62 "Every response family reached"

let private executeOutcomes () =
    let cases =
        [
            ExecuteOutcome.Accepted receipt, 0
            ExecuteOutcome.AlreadyAccepted receipt, 0
            ExecuteOutcome.Rejected Rejection.CaseVoided, 2
            ExecuteOutcome.Failed CoreFault.StoreUnavailable, 3
            ExecuteOutcome.Cancelled, 130
            ExecuteOutcome.OutcomeUnknown(id, digest), 4
        ]

    for value, code in cases do
        for endpoint in [ "command.execute"; "case.steward" ] do
            let encoded = encode endpoint (OutcomeCodec.execute value)
            let frame = Frames.result endpoint encoded |> accepted |> Frames.decode |> accepted
            Expect.equal (Frames.exitCode frame) code "Outcome selects exit"

    let erased =
        { receipt with
            Revision = 2L
            Act = AcceptedAct.Stewardship StewardshipKind.Erase
            Disposition = None
            Snapshot = None
        }

    let decoded =
        admitted "case.steward" (OutcomeCodec.execute (ExecuteOutcome.Accepted erased))

    Expect.equal
        (decoded.GetProperty("outcome").GetProperty("data").GetProperty("snapshot").ValueKind)
        JsonValueKind.Null
        "Erasure has no snapshot"

let private authorityOutcomes () =
    let values =
        [
            AuthorityOutcome.Applied(id, position, id)
            AuthorityOutcome.AlreadyApplied(id, position, id)
            AuthorityOutcome.Rejected Rejection.LastOwner
            AuthorityOutcome.Failed CoreFault.SchemaMismatch
            AuthorityOutcome.Cancelled
            AuthorityOutcome.OutcomeUnknown id
        ]

    for endpoint in [ "authority.register"; "authority.setGrant"; "authority.setEnabled" ] do
        for value in values do
            admitted endpoint (OutcomeCodec.authority value) |> ignore

    for value in List.take 5 values do
        admitted "authority.observe" (OutcomeCodec.authority value) |> ignore

    Expect.throws
        (fun () ->
            encode
                "authority.observe"
                (OutcomeCodec.authority (AuthorityOutcome.OutcomeUnknown id))
            |> ignore)
        "Observe cannot be uncertain"

let private caseQueries () =
    admitted
        "case.get"
        (OutcomeCodec.currentCase (
            QueryOutcome.Succeeded(
                Lookup.Found(
                    CaseRecord.Live(
                        Claim.view (opened ()),
                        CaseDisposition.Active,
                        [ CommandKind.Close ]
                    )
                )
            )
        ))
    |> ignore

    admitted
        "case.get"
        (OutcomeCodec.currentCase (
            QueryOutcome.Succeeded(Lookup.Found(CaseRecord.Erased("UNIT-001", 2L)))
        ))
    |> ignore

    admitted
        "case.get"
        (OutcomeCodec.currentCase (QueryOutcome.Succeeded(Lookup.NotFound "UNIT-001")))
    |> ignore

let private casePage () =
    admitted
        "case.list"
        (OutcomeCodec.caseList (
            QueryOutcome.Succeeded
                {
                    Items =
                        [
                            CaseSummary.Live(
                                "UNIT-001",
                                1L,
                                CaseStatus.Opened,
                                CaseDisposition.Voided
                            )
                            CaseSummary.Erased("UNIT-002", 2L)
                        ]
                    NextCursor = Some "cursor"
                    AvailableCommands = []
                }
        ))
    |> ignore

let private ledgerQueries () =
    admitted
        "case.history"
        (OutcomeCodec.history (
            QueryOutcome.Succeeded(
                Lookup.Found
                    {
                        Entries =
                            [
                                HistoryEntry.FullEntry receipt
                                HistoryEntry.SummaryEntry
                                    {
                                        OperationId = id
                                        Position = position
                                        Revision = 1L
                                        Act = receipt.Act
                                        RecordedAt = receipt.RecordedAt
                                        RecordedBy = id
                                    }
                            ]
                        NextCursor = None
                    }
            )
        ))
    |> ignore

    admitted
        "operation.observe"
        (OutcomeCodec.operation (QueryOutcome.Succeeded(Lookup.Found receipt)))
    |> ignore

let private discoveryQueries () =
    admitted
        "definition"
        (OutcomeCodec.definition (
            QueryOutcome.Succeeded
                {
                    Contract = SemanticContract.current
                    SemanticFingerprint = SemanticContract.fingerprint SemanticContract.current
                    Runtime =
                        {
                            ProductVersion = BuildIdentity.current.Version
                            EffectiveBusinessDate = today
                            TimeZoneId = "Etc/UTC"
                        }
                }
        ))
    |> ignore

    admitted "session" (OutcomeCodec.session true (Some "token")) |> ignore
    admitted "session.logout" (OutcomeCodec.logout ()) |> ignore

let private diagnostics () =
    let refusals =
        [
            Rejection.IdempotencyConflict
            Rejection.ResourceUnavailable
            Rejection.CaseVoided
            Rejection.CaseErased
            Rejection.InvalidReason
            Rejection.AlreadyVoided
            Rejection.NotVoided
            Rejection.LastOwner
            Rejection.ActorAlreadyRegistered
            Rejection.ActorUnknown
            Rejection.HumanPrincipalRequired
            Rejection.PageLimitOutOfRange 100
            Rejection.InvalidCaseListCursor
            Rejection.InvalidHistoryCursor
        ]

    for value in refusals do
        admitted "case.get" (OutcomeCodec.currentCase (QueryOutcome.Rejected value))
        |> ignore

    for value, _ in CoreFaults.all do
        admitted "case.get" (OutcomeCodec.currentCase (QueryOutcome.Failed value))
        |> ignore

let private rejectFrames () =
    for text in
        [
            "null"
            "[]"
            "{}"
            "{\"endpoint\":\"definition\",\"endpoint\":\"definition\",\"input\":{}}"
            "{\"endpoint\":\"case.unknown\",\"input\":{}}"
            "{\"endpoint\":\"definition\",\"input\":{\"secret\":\"private\"}}"
            "{\"endpoint\":\"case.get\",\"input\":{\"caseReference\":\"\\ud800\"}}"
        ] do
        Expect.isError (Frames.parse (bytes text)) "Malformed, duplicate or unknown input refused"

    for blob in [ [| 0xffuy |]; Array.zeroCreate<byte>(16 * 1024 * 1024 + 1) ] do
        Expect.isError (Frames.parse blob) "UTF-8 and byte bound"

    let original = bytes "{ \"endpoint\": \"definition\", \"input\": { } }"
    let parsed = Frames.parse original |> accepted
    original[0] <- 0uy
    Expect.equal ((Frames.bytes parsed)[0]) (byte '{') "Defensive frame copy"
    let copy = Frames.bytes parsed
    copy[0] <- 0uy
    Expect.equal ((Frames.bytes parsed)[0]) (byte '{') "Caller cannot change retained bytes"

let private handshakeAndFailures () =
    let fingerprint = WireContractFingerprint.value EndpointCatalog.fingerprint
    Expect.equal fingerprint.Length 64 "SHA-256"
    Expect.isTrue (EndpointCatalog.matches (Some fingerprint)) "Exact build"

    for value in [ None; Some(fingerprint.ToUpperInvariant()); Some(String.replicate 64 "0") ] do
        Expect.isFalse (EndpointCatalog.matches value) "Mismatch refuses dispatch"

    for reason in HostFailures.all do
        let wire = HostFailures.encode reason
        use parsed = JsonDocument.Parse wire

        Expect.isTrue
            (SchemaValueValidation.verify HostFailures.schema parsed.RootElement)
            "Typed host failure"

        Expect.equal
            (parsed.RootElement.GetProperty("status").GetInt32())
            (HostFailures.status reason)
            "Correlated status"

    Expect.equal
        (Frames.serviceFailure "definition" HostFailure.RateLimited
         |> Frames.decode
         |> accepted
         |> Frames.exitCode)
        3
        "Service failure"

    Expect.equal
        (Frames.protocolFailure "INVALID_FRAME"
         |> Frames.decode
         |> accepted
         |> Frames.exitCode)
        2
        "Protocol failure"

    Expect.equal
        (Frames.localFailure "CLI_CONFIGURATION_INVALID"
         |> Frames.decode
         |> accepted
         |> Frames.exitCode)
        3
        "Local failure"

let private decoderProperty =
    let seeds =
        EndpointCatalog.all
        |> List.collect (fun e ->
            SchemaSamples.variants e.Request @ SchemaSamples.variants e.Response)

    let generator =
        Gen.choice (DecoderCorpora.jsonLike :: List.map DecoderCorpora.mutated seeds)

    property {
        let! value = generator

        let total decoder =
            try
                decoder value |> ignore
                true
            with _ ->
                false

        return
            total Frames.parse
            && total Frames.decode
            && (EndpointCatalog.all
                |> List.forall (fun e ->
                    total (EndpointCatalog.admitRequest e.Identifier)
                    && total (EndpointCatalog.admitResponse e.Identifier)))
    }

let private previewAndRoles () =
    let view = Claim.view (opened ())

    let review =
        {
            Before = None
            Proposed = view
            Changes =
                [
                    {
                        FieldName = "caseReference"
                        Before = None
                        After = Some "UNIT-001"
                    }
                ]
            Context =
                {
                    ProductVersion = BuildIdentity.current.Version
                    EffectiveBusinessDate = today
                    TimeZoneId = "Etc/UTC"
                }
            IsAdvisory = true
        }

    for value in
        [
            PreviewOutcome.Reviewed(review, digest)
            PreviewOutcome.AlreadyAccepted receipt
            PreviewOutcome.Rejected Rejection.InvalidReason
            PreviewOutcome.Failed CoreFault.StoreIntegrityError
            PreviewOutcome.Cancelled
        ] do
        admitted "command.preview" (OutcomeCodec.preview value) |> ignore

    let input role =
        bytes (
            "{\"eventId\":\""
            + id.ToString("D")
            + "\",\"principal\":{\"kind\":\"HUMAN\",\"issuer\":\"https://identity.example.test/\",\"subject\":\"steward\"},\"role\":\""
            + role
            + "\",\"scope\":{\"type\":\"INSTALLATION\"},\"active\":true}"
        )

    for token in [ "OWNER"; "CASE_READER"; "CASE_EDITOR"; "DATA_STEWARD"; "AUDITOR" ] do
        EndpointCatalog.admitRequest "authority.setGrant" (input token)
        |> accepted
        |> ignore

    for token in [ "READER"; "EDITOR"; "STEWARD"; "administrator" ] do
        Expect.isError
            (EndpointCatalog.admitRequest "authority.setGrant" (input token))
            "No alternate role names"

let private sequenceBoundary () =
    admitted
        "command.execute"
        (OutcomeCodec.execute (
            ExecuteOutcome.Accepted
                { receipt with
                    Position =
                        { position with
                            Sequence = Int64.MaxValue
                        }
                }
        ))
    |> ignore

    for value in [ "0"; "-1"; "9223372036854775808"; "01" ] do
        let sample =
            encode "command.execute" (OutcomeCodec.execute (ExecuteOutcome.Accepted receipt))

        let node = JsonNode.Parse sample |> nonNull

        let outcome = node["outcome"] |> nonNull
        let data = outcome["data"] |> nonNull
        let position = data["position"] |> nonNull
        position.AsObject()["sequence"] <- JsonValue.Create value

        Expect.isError
            (EndpointCatalog.admitResponse "command.execute" (bytes (node.ToJsonString())))
            "Sequence must be a positive representable BIGINT"

[<Tests>]
let tests =
    testList
        "service contract"
        [
            testCase
                "all frame and HTTP decoders are total on arbitrary, hostile and mutated bytes"
                (fun () -> PropertyHarness.run "CC-FUZZ-INVOCATION-001" decoderProperty)
            testCase
                "preview codec and authority role tokens preserve the core definitions"
                previewAndRoles
            testCase
                "ledger positions admit positive BIGINT maximum and refuse zero or overflow"
                sequenceBoundary
            testCase
                "one catalogue defines fourteen literal routes and every durable mutation"
                exactCatalogue
            testCase
                "all request alternatives admit, preserve bytes and reject extra members"
                requestAlternatives
            testCase
                "all response alternatives round-trip and reject extra frame members"
                responseAlternatives
            testCase
                "execute outcomes select exits and erased receipts have no snapshot"
                executeOutcomes
            testCase
                "authority outcomes cover mutations and reject unknown observation"
                authorityOutcomes
            testCase "typed query codecs preserve live, voided, erased and ledger views" (fun () ->
                caseQueries ()
                casePage ()
                ledgerQueries ()
                discoveryQueries ())
            testCase "the closed diagnostic sets have complete schema and presentation" diagnostics
            testCase
                "strict frame decoding rejects hostile bytes and preserves defensive copies"
                rejectFrames
            testCase
                "fingerprint handshake and typed failure guards have rejecting controls"
                handshakeAndFailures
        ]
