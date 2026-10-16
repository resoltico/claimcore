module ClaimCore.Tests.ContractTests

open ClaimCore.ClientProtocol

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

module Frames = ClaimCore.ClientContract.Frames

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
    (if
         endpoint = "case.preview"
         || endpoint = "case.execute"
         || endpoint = "authority.execute"
     then
         OutcomeCodec.encodeOperation endpoint id value
     else
         OutcomeCodec.encode endpoint value)
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
            "authority.grants"
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

let private pending endpoint =
    let definition = EndpointCatalog.tryFind endpoint |> Option.get

    let input =
        SchemaSamples.variants definition.Request
        |> List.head
        |> fun bytes -> JsonNode.Parse(bytes) |> nonNull

    if input.AsObject().ContainsKey "operationId" then
        input["operationId"] <- JsonValue.Create(id.ToString("D"))

    if input.AsObject().ContainsKey "caseReference" then
        input["caseReference"] <- JsonValue.Create("UNIT-001")

    let frame = JsonObject()
    frame.Add("endpoint", JsonValue.Create endpoint)
    frame.Add("input", input)
    frame.ToJsonString() |> bytes |> Frames.parse |> accepted

let private requestAlternatives () =
    let mutable count = 0

    for endpoint in EndpointCatalog.all do
        for sample in SchemaSamples.variants endpoint.Request do
            HttpInputSupport.parse sample (fun root ->
                Expect.isTrue (SchemaValueValidation.verify endpoint.Request root) "Wire syntax")
            |> accepted
            |> ignore

            let frame = JsonObject()
            frame.Add("endpoint", JsonValue.Create endpoint.Identifier)
            frame.Add("input", JsonNode.Parse sample)

            let parsed =
                ClaimCore.ClientContract.Frames.parse (bytes (frame.ToJsonString())) |> accepted

            Expect.equal
                (ClaimCore.ClientContract.Frames.inputBytes parsed)
                sample
                "Exact raw input retained"

            Expect.isError
                (EndpointCatalog.admitRequest endpoint.Identifier (mutate sample "extra" "x"))
                "Closed request"

            count <- count + 1

    Expect.equal count 36 "Every case and authority act is sampled"

let private responseAlternatives () =
    let mutable count = 0

    for endpoint in EndpointCatalog.all do
        for sample in SchemaSamples.variants endpoint.Response do
            EndpointCatalog.admitResponse endpoint.Identifier sample |> accepted |> ignore

            let native =
                ClaimCore.ClientContract.EndpointCatalog.tryFind endpoint.Identifier
                |> Option.get

            HttpInputSupport.parse sample (fun root ->
                SchemaValueValidation.verify native.Response root)
            |> accepted
            |> fun valid ->
                Expect.isTrue valid "Generated native response schema admits every server variant"

            Expect.isError
                (EndpointCatalog.admitResponse endpoint.Identifier (mutate sample "extra" "x"))
                "Closed response"

            count <- count + 1

    Expect.equal count 47 "Every response alternative is sampled"

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
            let encoded = OutcomeCodec.encodeOperation endpoint id (OutcomeCodec.execute value)

            let frame =
                Frames.result (pending endpoint) encoded
                |> accepted
                |> Frames.decode
                |> accepted

            Expect.equal (Frames.exitCode frame) exit "Shared mutation exits"

    for endpoint, wrong in [ "case.execute", authority; "authority.execute", receipt ] do
        Expect.throws
            (fun () ->
                OutcomeCodec.encodeOperation
                    endpoint
                    id
                    (OutcomeCodec.execute (ExecuteOutcome.Accepted wrong))
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

let private currentCaseCodecs () =
    let snapshot = Case.snapshot (opened ())

    admitted
        "case.get"
        (OutcomeCodec.currentCase (
            QueryOutcome.Succeeded
                {
                    Snapshot = snapshot
                    AvailableActs = [ CaseActKind.Close ]
                    Offers = []
                    HistoryDetails = []
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
                    Offers = []
                    HistoryDetails = []
                }
        ))
    |> ignore

let private queryCodecs () =
    currentCaseCodecs ()

    admitted
        "case.list"
        (OutcomeCodec.caseList (
            QueryOutcome.Succeeded
                {
                    Items = [ CaseSummary.Erased("UNIT-001", 2L) ]
                    Offers = []
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
                    Offers = []
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

let private actionExpectations =
    [
        Rejection.Case(
            CaseRefusal.InvalidInput(
                InputTarget.Reason,
                InputViolation.Text TextViolation.NonBlankRequired
            )
        ),
        RecommendedAction.CorrectInput,
        "CORRECT_INPUT",
        Some "reason",
        None
        Rejection.PageLimitOutOfRange 50,
        RecommendedAction.CorrectInput,
        "CORRECT_INPUT",
        Some "limit",
        None
        Rejection.InvalidCursor, RecommendedAction.ReadCurrent, "READ_CURRENT", Some "cursor", None
        Rejection.Case(CaseRefusal.RevisionConflict 7L),
        RecommendedAction.ReadCurrent,
        "READ_CURRENT",
        None,
        Some 7L
    ]

let private actionEnums () =
    for refusal, expected, _, _, _ in actionExpectations do
        Expect.equal
            refusal.Action
            expected
            "Input correction and current-state recovery are distinct"

let private actionTokens () =
    for refusal, _, expected, field, revision in actionExpectations do
        let result =
            admitted "case.get" (OutcomeCodec.currentCase (QueryOutcome.Rejected refusal))

        let data = result.GetProperty("outcome").GetProperty("data")

        Expect.equal
            (data.GetProperty("recommendedAction").GetString())
            expected
            "Literal wire guidance"

        Expect.equal
            (data.GetProperty("field").GetString())
            (Option.toObj field)
            "Serialized field metadata is retained"

        let encodedRevision = data.GetProperty("actualRevision").GetRawText()
        let expectedRevision = revision |> Option.map string |> Option.defaultValue "null"
        Expect.equal encodedRevision expectedRevision "Serialized revision metadata is retained"
        Expect.equal refusal.Field field "Field metadata is retained"
        Expect.equal refusal.ActualRevision revision "Revision metadata is retained"

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
        (Frames.serviceFailure (pending "context") HostFailure.RateLimited
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
                OutcomeCodec.encodeOperation
                    "case.execute"
                    id
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
                        Offers = []
                        HistoryDetails = []
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

let private authorityWire kind issuer subject =
    JsonSerializer.SerializeToUtf8Bytes
        {|
            operationId = id.ToString("D")
            target =
                {|
                    kind = kind
                    issuer = issuer
                    subject = subject
                |}
            act = {| kind = "REGISTER_ACTOR" |}
        |}

let private principalAdmission () =
    let requestSchema =
        (EndpointCatalog.tryFind "authority.execute" |> Option.get).Request

    let expected =
        [
            true, "HUMAN", "https://issuer.example/", String.replicate 512 "a"
            false, "HUMAN", "https://issuer.example/", String.replicate 513 "a"
            true, "HUMAN", "https://issuer.example/", String.replicate 256 "\U0001f642"
            false, "HUMAN", "https://issuer.example/", String.replicate 257 "\U0001f642"
            true, "SERVICE", "https://issuer.example/", String.replicate 256 "a"
            false, "SERVICE", "https://issuer.example/", String.replicate 257 "a"
            true,
            "HUMAN",
            "https://issuer.example/"
            + String.replicate (2048 - "https://issuer.example/".Length) "a",
            "subject"
            false,
            "HUMAN",
            "https://issuer.example/"
            + String.replicate (2049 - "https://issuer.example/".Length) "a",
            "subject"
            false, "HUMAN", "https://issuer.example", "subject"
            false, "HUMAN", "https://user@issuer.example/", "subject"
            false, "HUMAN", "https://issuer.example/?q=x", "subject"
            false, "HUMAN", "https://issuer.example/#x", "subject"
            false, "HUMAN", "https://issuer.example/", "\u2003"
            true, "HUMAN", "https://issuer.example/", "Unicode \U0001f642 subject"
        ]

    for admitted, kind, issuer, subject in expected do
        let wire = authorityWire kind issuer subject

        HttpInputSupport.parse wire (fun root ->
            Expect.isTrue
                (SchemaValueValidation.verify requestSchema root)
                "Generic wire syntax accepts identity text")
        |> accepted
        |> ignore

        Expect.equal
            (EndpointCatalog.admitRequest "authority.execute" wire |> Result.isOk)
            admitted
            "Only authoritative principal admission judges identity semantics"

    let invalidKind = authorityWire "UNKNOWN" "https://issuer.example/" "subject"

    Expect.isError
        (EndpointCatalog.admitRequest "authority.execute" invalidKind)
        "Closed principal kind"

let private authorityFieldPresentation () =
    use document = JsonDocument.Parse((ContractDiscovery.descriptors ()).Bytes)
    use presentation = JsonDocument.Parse(ContractDiscovery.presentation ())
    let root = document.RootElement
    let fields = root.GetProperty("authorityFields").EnumerateArray() |> Seq.toList

    let names =
        fields
        |> List.map (fun field -> field.GetProperty("name").GetString() |> nonNull)

    Expect.equal
        names.Length
        (Set.ofList names).Count
        "Protocol presentation identifiers are unique"

    Expect.equal
        (root.GetProperty("fields").GetArrayLength())
        13
        "Protocol labels add no business fields"

    let applicationNames =
        AuthorityOperationDefinitions.target.Name
        :: (AuthorityOperationDefinitions.all
            |> List.collect (fun definition -> definition.Inputs |> List.map _.Name))

    let technicalNames =
        [ "operationId"; "expectedRevision"; "act"; "kind"; "issuer"; "subject" ]

    Expect.equal
        (Set.ofList names)
        (Set.ofList (technicalNames @ applicationNames))
        "Every generic technical and owned authority input has presentation metadata"

    for field in fields do
        let name = field.GetProperty("name").GetString() |> nonNull
        let labelId = field.GetProperty("labelId").GetString() |> nonNull
        let label = field.GetProperty("label").GetString()
        Expect.equal labelId ("authority.field." + name + ".label") "Stable message identifier"

        let defaultLabel =
            presentation.RootElement.GetProperty(labelId).EnumerateArray() |> Seq.head

        Expect.equal
            (defaultLabel.GetProperty("literal").GetString())
            label
            "Default English is projected from the same field definition"

    let kind =
        fields
        |> List.find (fun field -> field.GetProperty("name").GetString() = "kind")

    Expect.equal (kind.GetProperty("label").GetString()) "Type" "Generic kinds render a human label"

[<Tests>]
let tests =
    testList
        "service contract"
        [
            testCase
                "generic authority input labels come from semantic and protocol owners"
                authorityFieldPresentation
            testCase
                "generic principal syntax preserves separate authoritative identity admission"
                principalAdmission
            testCase "current text admission is exposed as a nonvalidating policy hint" policyHints
            testCase
                "retained text passes wire rendering and reaffirmation while changed policy violations refuse"
                retainedText
            testCase "one catalogue defines twelve routes and both durable mutations" exactCatalogue
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
            testCase "input and cursor refusals retain core action guidance" actionEnums
            testCase "input and cursor refusals retain serialized action guidance" actionTokens
            testCase "strict decoders reject malformed input and preserve copies" rejectFrames
            testCase
                "handshake and host failure guards have rejecting controls"
                handshakeAndFailures
            testCase "integer transport checks raw lexical forms and safe boundaries" integerLexemes
            testCase "all frame and HTTP decoders are total for bounded hostile bytes" (fun () ->
                PropertyHarness.run "CC-FUZZ-INVOCATION-001" decoderProperty)
        ]
