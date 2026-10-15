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
open ClaimCore.RecordFormat
open ClaimCore.Tests.Fixtures

let private bytes (value: string) = Encoding.UTF8.GetBytes value
let private id = Guid.Parse "20000000-0000-4000-8000-000000000001"
let private digest = String.replicate 64 "a"
let private position = { Sequence = 1L; EntryHash = digest }

let private receipt =
    {
        OperationId = id
        Position = position
        Scope =
            ReceiptScope.Case
                {
                    Reference = "UNIT-001"
                    Revision = 1L
                    Act = CaseActKind.Open
                    Snapshot = SnapshotDisclosure.Disclosed (Case.snapshot (opened ())).View
                }
        RecordedAt = DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero)
        RecordedBy = id
    }

let private authority =
    { receipt with
        Scope =
            ReceiptScope.Authority
                {
                    TargetActorId = id
                    Act = RecordedAuthorityKind.RegisterActor
                }
    }

let private admitted endpoint value =
    OutcomeCodec.encode endpoint value
    |> EndpointCatalog.admitResponse endpoint
    |> accepted

let private mutate bytes key value =
    let node = JsonNode.Parse(bytes: byte array) |> nonNull
    node.AsObject().Add(key, JsonValue.Create<string> value)
    bytes |> ignore
    Encoding.UTF8.GetBytes(node.ToJsonString())

let private exactCatalogue () =
    Expect.equal
        (EndpointCatalog.all |> List.map _.Identifier)
        [
            "session"
            "session.logout"
            "context"
            "case.get"
            "case.list"
            "case.history"
            "case.preview"
            "case.execute"
            "operation.observe"
            "authority.execute"
            "authority.list"
        ]
        "Exact inventory"

    for endpoint in EndpointCatalog.all do
        Expect.equal endpoint.Path ("/api/" + endpoint.Identifier) "Literal route"

        Expect.equal
            endpoint.Method
            (if endpoint.Identifier = "session" then "GET" else "POST")
            "Method"

    Expect.equal
        (EndpointCatalog.all |> List.filter _.Mutates |> List.map _.Identifier)
        [ "case.execute"; "authority.execute" ]
        "Durable mutations"

let private requestAlternatives () =
    let mutable count = 0

    for endpoint in EndpointCatalog.all do
        for sample in SchemaSamples.variants endpoint.Request do
            EndpointCatalog.admitRequest endpoint.Identifier sample |> accepted |> ignore
            let frame = JsonObject()
            frame.Add("endpoint", JsonValue.Create endpoint.Identifier)
            frame.Add("input", JsonNode.Parse sample)
            let parsed = Frames.parse (bytes (frame.ToJsonString())) |> accepted
            Expect.equal (Frames.inputBytes parsed) sample "Exact raw input retained"

            Expect.isError
                (EndpointCatalog.admitRequest endpoint.Identifier (mutate sample "extra" "x"))
                "Closed request"

            count <- count + 1

    Expect.equal count 35 "Every case and authority act is sampled"

let private responseAlternatives () =
    let mutable count = 0

    for endpoint in EndpointCatalog.all do
        for sample in SchemaSamples.variants endpoint.Response do
            EndpointCatalog.admitResponse endpoint.Identifier sample |> accepted |> ignore
            let frame = Frames.result endpoint.Identifier sample |> accepted
            Expect.isOk (Frames.decode frame) "Admitted response round-trips"
            Expect.isError (Frames.decode (mutate frame "extra" "x")) "Closed frame"
            count <- count + 1

    Expect.equal count 43 "Every response alternative is sampled"

let private executeOutcomes () =
    for endpoint, receipt in [ "case.execute", receipt; "authority.execute", authority ] do
        for value, exit in
            [
                ExecuteOutcome.Accepted receipt, 0
                ExecuteOutcome.AlreadyAccepted receipt, 0
                ExecuteOutcome.Rejected Rejection.LastOwner, 2
                ExecuteOutcome.Failed CoreFault.StoreUnavailable, 3
                ExecuteOutcome.Cancelled, 130
                ExecuteOutcome.OutcomeUnknown(id, digest), 4
            ] do
            let encoded = OutcomeCodec.encode endpoint (OutcomeCodec.execute value)
            let frame = Frames.result endpoint encoded |> accepted |> Frames.decode |> accepted
            Expect.equal (Frames.exitCode frame) exit "Shared mutation exits"

    for endpoint, wrong in [ "case.execute", authority; "authority.execute", receipt ] do
        Expect.throws
            (fun () ->
                OutcomeCodec.encode endpoint (OutcomeCodec.execute (ExecuteOutcome.Accepted wrong))
                |> ignore)
            "A mutation receipt must match its endpoint scope"

let private disclosures () =
    for disclosure, tag in
        [ SnapshotDisclosure.Withheld, "WITHHELD"; SnapshotDisclosure.Erased, "ERASED" ] do
        let target =
            match receipt.Scope with
            | ReceiptScope.Case value -> value
            | _ -> failtest "Case receipt"

        let value =
            { receipt with
                Scope = ReceiptScope.Case { target with Snapshot = disclosure }
            }

        let decoded =
            admitted "operation.observe" (OutcomeCodec.operation (QueryOutcome.Succeeded value))

        Expect.equal
            (decoded
                .GetProperty("outcome")
                .GetProperty("data")
                .GetProperty("snapshot")
                .GetProperty("tag")
                .GetString())
            tag
            "Disclosure is explicit"

let private queryCodecs () =
    let snapshot = Case.snapshot (opened ())

    admitted
        "case.get"
        (OutcomeCodec.currentCase (
            QueryOutcome.Succeeded
                {
                    Snapshot = snapshot
                    AvailableActs = [ CaseActKind.Close ]
                }
        ))
    |> ignore

    admitted
        "case.get"
        (OutcomeCodec.currentCase (
            QueryOutcome.Succeeded
                {
                    Snapshot = { snapshot with View = CaseView.Erased }
                    AvailableActs = []
                }
        ))
    |> ignore

    admitted
        "case.list"
        (OutcomeCodec.caseList (
            QueryOutcome.Succeeded
                {
                    Items = [ CaseSummary.Erased("UNIT-001", 2L) ]
                    NextCursor = None
                }
        ))
    |> ignore

    admitted
        "case.history"
        (OutcomeCodec.history (
            QueryOutcome.Succeeded
                {
                    Entries = [ HistoryEntry.FullEntry receipt ]
                    NextCursor = None
                }
        ))
    |> ignore

    admitted "operation.observe" (OutcomeCodec.operation (QueryOutcome.Succeeded authority))
    |> ignore

    admitted
        "authority.list"
        (OutcomeCodec.actors (QueryOutcome.Succeeded { Items = []; NextCursor = None }))
    |> ignore

let private contextCodecs () =
    admitted
        "context"
        (OutcomeCodec.context (
            QueryOutcome.Succeeded
                {
                    Runtime =
                        {
                            ProductVersion = "0.1.0"
                            EffectiveBusinessDate = today
                            TimeZoneId = "Etc/UTC"
                        }
                    Capabilities = Capabilities.all
                }
        ))
    |> ignore

    admitted "session" (OutcomeCodec.session true (Some "token")) |> ignore
    admitted "session.logout" (OutcomeCodec.logout ()) |> ignore

let private previewCodecs () =
    let snapshot = Case.snapshot (opened ())

    let review =
        {
            Before = None
            Proposed = snapshot
            Changes = []
            Context =
                {
                    ProductVersion = "0.1.0"
                    EffectiveBusinessDate = today
                    TimeZoneId = "Etc/UTC"
                }
        }

    for value in
        [
            PreviewOutcome.Reviewed(review, digest)
            PreviewOutcome.AlreadyAccepted receipt
            PreviewOutcome.Rejected Rejection.ActNotPermitted
            PreviewOutcome.Failed CoreFault.StoreIntegrityError
            PreviewOutcome.Cancelled
        ] do
        admitted "case.preview" (OutcomeCodec.preview value) |> ignore

let private diagnostics () =
    for identifier, _ in RejectionDiagnosticIds.all do
        Expect.isNonEmpty
            (RejectionPresentation.template identifier)
            "Every diagnostic has outward text"

    for fault, _ in CoreFaults.all do
        admitted "case.get" (OutcomeCodec.currentCase (QueryOutcome.Failed fault))
        |> ignore

    for value in
        [
            Rejection.LedgerExhausted
            Rejection.InvalidCursor
            Rejection.LastOwner
            Rejection.ActNotPermitted
            Rejection.ResourceUnavailable
        ] do
        let result =
            admitted "case.get" (OutcomeCodec.currentCase (QueryOutcome.Rejected value))

        Expect.isFalse
            (result.GetProperty("outcome").GetProperty("data").TryGetProperty("code") |> fst)
            "No coarse refusal code"

let private rejectFrames () =
    for value in
        [
            "null"
            "[]"
            "{}"
            "{\"endpoint\":\"context\",\"endpoint\":\"context\",\"input\":{}}"
            "{\"endpoint\":\"unknown\",\"input\":{}}"
            "{\"endpoint\":\"context\",\"input\":{\"secret\":true}}"
            "{\"endpoint\":\"case.get\",\"input\":{\"caseReference\":\"\\ud800\"}}"
        ] do
        Expect.isError (Frames.parse (bytes value)) "Malformed, duplicate and unknown refuse"

    for blob in [ [| 0xffuy |]; Array.zeroCreate<byte>(16 * 1024 * 1024 + 1) ] do
        Expect.isError (Frames.parse blob) "UTF8 and byte bounds"

    let original = bytes "{ \"endpoint\":\"context\", \"input\":{} }"
    let frame = Frames.parse original |> accepted
    original[0] <- 0uy
    let copy = Frames.bytes frame
    copy[0] <- 0uy
    Expect.equal ((Frames.bytes frame)[0]) (byte '{') "Defensive copies"

let private handshakeAndFailures () =
    let fingerprint = WireContractFingerprint.value EndpointCatalog.fingerprint
    Expect.equal fingerprint.Length 64 "SHA256"
    Expect.isTrue (EndpointCatalog.matches (Some fingerprint)) "Exact identity"

    for value in [ None; Some(fingerprint.ToUpperInvariant()); Some(String.replicate 64 "0") ] do
        Expect.isFalse (EndpointCatalog.matches value) "Mismatch refuses"

    for failure in HostFailures.all do
        Expect.equal
            (HostFailures.decode (HostFailures.encode failure) |> accepted)
            failure
            "Host failure round-trip"

    Expect.equal
        (Frames.serviceFailure "context" HostFailure.RateLimited
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
        |> List.collect (fun endpoint ->
            SchemaSamples.variants endpoint.Request
            @ SchemaSamples.variants endpoint.Response)

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
                |> List.forall (fun endpoint ->
                    total (EndpointCatalog.admitRequest endpoint.Identifier)
                    && total (EndpointCatalog.admitResponse endpoint.Identifier)))
    }

let private integerLexemes () =
    let wire =
        RequestRecord.encodeWire (request 0L (BusinessCommand.Open registration))
        |> Encoding.UTF8.GetString

    for value in [ "-1"; "9007199254740992"; "1.0"; "1e0"; "\"1\"" ] do
        Expect.isError
            (EndpointCatalog.admitRequest
                "case.execute"
                (bytes (wire.Replace("\"expectedRevision\":0", "\"expectedRevision\":" + value))))
            "Strict raw integer request"

    for value in [ "0"; "-0"; "9007199254740991" ] do
        Expect.isOk
            (EndpointCatalog.admitRequest
                "case.execute"
                (bytes (wire.Replace("\"expectedRevision\":0", "\"expectedRevision\":" + value))))
            "Safe integer boundary"

    for sequence in [ 0L; SafeInteger.Maximum + 1L ] do
        Expect.throws
            (fun () ->
                OutcomeCodec.encode
                    "case.execute"
                    (OutcomeCodec.execute (
                        ExecuteOutcome.Accepted
                            { receipt with
                                Position = { position with Sequence = sequence }
                            }
                    ))
                |> ignore)
            "Accepted sequence bounds"

let private retainedText () =
    for name in [ String.replicate 201 "A"; "Earlier\u202eName" ] do
        let original = Case.snapshot (opened ())

        let fields =
            { viewFields original with
                ClaimantName = name
            }

        let snapshot =
            { original with
                View = CaseView.Live(fields, Disposition.Active)
            }

        let prior = Case.ofAccepted snapshot |> accepted

        admitted
            "case.get"
            (OutcomeCodec.currentCase (
                QueryOutcome.Succeeded
                    {
                        Snapshot = snapshot
                        AvailableActs = []
                    }
            ))
        |> ignore

        let reaffirm =
            request
                1L
                (BusinessCommand.AmendRegistration
                    { registration with
                        ClaimantName = name
                        InsurerName = "Changed insurer"
                    })

        let wire = RequestRecord.encodeWire reaffirm
        EndpointCatalog.admitRequest "case.execute" wire |> accepted |> ignore
        Case.decide today reaffirm (Some prior) |> accepted |> ignore

        let changed =
            { reaffirm with
                Act =
                    CaseAct.Business(
                        BusinessCommand.AmendRegistration
                            { registration with
                                ClaimantName = name + "B"
                            }
                    )
            }

        Expect.isError
            (Case.decide today changed (Some prior))
            "Changed new assertion follows admission"

let private policyHints () =
    use descriptors =
        JsonDocument.Parse(CanonicalContract.bytes (ContractDescriptors.render ()))

    let claimant =
        descriptors.RootElement.GetProperty("fields").EnumerateArray()
        |> Seq.find (fun field -> field.GetProperty("name").GetString() = "claimantName")

    Expect.equal
        (claimant
            .GetProperty("scalar")
            .GetProperty("policy")
            .GetProperty("maximumCharacters")
            .GetInt32())
        200
        "Current policy is a nonvalidating hint"

[<Tests>]
let tests =
    testList
        "service contract"
        [
            testCase "current text admission is exposed as a nonvalidating policy hint" policyHints
            testCase
                "retained text passes wire rendering and reaffirmation while changed policy violations refuse"
                retainedText
            testCase "one catalogue defines eleven routes and both durable mutations" exactCatalogue
            testCase
                "all act request alternatives preserve bytes and reject extra members"
                requestAlternatives
            testCase
                "all response alternatives round-trip and reject extra members"
                responseAlternatives
            testCase "both mutation scopes share outcomes and exit codes" executeOutcomes
            testCase "withheld and erased snapshots remain distinct" disclosures
            testCase "queries encode current cases authority and context" (fun () ->
                queryCodecs ()
                contextCodecs ())
            testCase "preview encodes review and replay outcomes" previewCodecs
            testCase "closed diagnostics have complete presentation and no coarse codes" diagnostics
            testCase "strict decoders reject malformed input and preserve copies" rejectFrames
            testCase
                "handshake and host failure guards have rejecting controls"
                handshakeAndFailures
            testCase "integer transport checks raw lexical forms and safe boundaries" integerLexemes
            testCase "all frame and HTTP decoders are total for bounded hostile bytes" (fun () ->
                PropertyHarness.run "CC-FUZZ-INVOCATION-001" decoderProperty)
        ]
