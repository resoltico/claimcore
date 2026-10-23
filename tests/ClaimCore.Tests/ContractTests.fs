module ClaimCore.Tests.ContractTests

open ClaimCore.Contract

open System
open System.Text
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Contract.Protocol
open ClaimCore.TestSupport
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private bytes (value: string) = Encoding.UTF8.GetBytes value
let private id = Guid.Parse "20000000-0000-4000-8000-000000000001"
let private digest = String.replicate 64 "a"

let private position: ClaimCore.Contract.LedgerPosition =
    { Sequence = 1L; EntryHash = digest }

let private receipt: ClaimCore.Contract.OperationReceipt =
    {
        OperationId = id
        Position = position
        Scope =
            ClaimCore.Contract.ReceiptScope.Case
                {
                    Reference = "UNIT-001"
                    Revision = 1L
                    Act = (CaseActKind.Business BusinessActKind.Open)
                    Snapshot =
                        ClaimCore.Contract.SnapshotDisclosure.Disclosed
                            (Case.snapshot (opened ())).View
                }
        RecordedAt = DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero)
        RecordedBy = id
    }

let private authority =
    { receipt with
        Scope =
            ClaimCore.Contract.ReceiptScope.Authority
                {
                    TargetActorId = id
                    Act = AuthorityKind.RegisterActor
                }
    }

let private admitted (value: WireReply) =
    let encoded = Replies.encode value

    Expect.equal
        (Replies.decode (WireReply.endpoint value) encoded)
        (Ok value)
        "Typed reply round trips"

    use document = JsonDocument.Parse(encoded.AsMemory())
    document.RootElement.Clone()

let private wireRegistration (value: Registration) : Registration =
    {
        IncidentDate = value.IncidentDate
        IncidentNotificationDate = value.IncidentNotificationDate
        IncidentCountry = value.IncidentCountry
        ClaimantName = value.ClaimantName
        InsurerName = value.InsurerName
        ClaimedAmount = value.ClaimedAmount
        ClaimedCurrency = value.ClaimedCurrency
    }

let private wireRequest values : ClaimCore.Contract.CaseRequest =
    {
        OperationId = id
        CaseReference = "UNIT-001"
        ExpectedRevision = 0L
        Act = ClaimCore.Contract.CaseAct.Open(wireRegistration values)
    }

let private contextCodec () =
    OutcomeMapping.context (
        ReadReply.Succeeded
            {
                Runtime =
                    {
                        ProductVersion = "0.10.0"
                        EffectiveBusinessDate = today
                        TimeZoneId = "Etc/UTC"
                    }
                Capabilities = []
                Views = []
                Offers = []
            }
    )
    |> admitted
    |> ignore

let private queryCodecs () =
    let snapshot = Case.snapshot (opened ())

    for view in
        [
            snapshot
            { snapshot with
                View = ClaimCore.Contract.CaseView.Erased
            }
        ] do
        OutcomeMapping.caseGet (
            ReadReply.Succeeded
                {
                    Snapshot = view
                    Advice = { Acts = []; History = [] }
                }
        )
        |> admitted
        |> ignore

    OutcomeMapping.caseList (
        ReadReply.Succeeded
            {
                Items = []
                NextCursor = None
                Offers = []
            }
    )
    |> admitted
    |> ignore

    OutcomeMapping.caseHistory (ReadReply.Succeeded { Entries = []; NextCursor = None })
    |> admitted
    |> ignore

    OutcomeMapping.operationObserve (ReadReply.Succeeded authority)
    |> admitted
    |> ignore

    OutcomeMapping.authorityList (ReadReply.Succeeded { Items = []; NextCursor = None })
    |> admitted
    |> ignore


let private executeOutcomes () =
    for map, receiptValue in
        [ OutcomeMapping.execute, receipt; OutcomeMapping.authorityExecute, authority ] do
        for outcome, exit in
            [
                ExecuteOutcome.Accepted receiptValue, 0
                ExecuteOutcome.AlreadyAccepted receiptValue, 0
                ExecuteOutcome.Rejected ClaimCore.Contract.Refusal.ActNotPermitted, 2
                ExecuteOutcome.Failed CoreFault.StoreUnavailable, 3
                ExecuteOutcome.Cancelled, 130
                ExecuteOutcome.OutcomeUnknown id, 4
            ] do
            let reply = map id outcome
            admitted reply |> ignore

            let frame =
                Frames.fromReply
                    {
                        OperationId = Some id
                        AttemptKnowledge =
                            Delivery.knowledge (WireReply.endpoint reply) (WireReply.attempt reply)
                        ResolutionEvidence =
                            Delivery.evidence (WireReply.endpoint reply) (WireReply.attempt reply)
                    }
                    reply

            Expect.equal
                (Frames.decode (Frames.encode frame))
                (Ok frame)
                "CLI frames share mutation outcomes"

            Expect.equal (Frames.exitCode frame) exit "Mutation scope preserves exit semantics"

let private disclosures () =
    for snapshot, tag in
        [ SnapshotDisclosure.Withheld, "WITHHELD"; SnapshotDisclosure.Erased, "ERASED" ] do
        let value =
            { receipt with
                Scope =
                    ClaimCore.Contract.ReceiptScope.Case
                        {
                            Reference = "UNIT-001"
                            Revision = 1L
                            Act = CaseActKind.Business BusinessActKind.Open
                            Snapshot = snapshot
                        }
            }

        let result = OutcomeMapping.operationObserve (ReadReply.Succeeded value) |> admitted

        Expect.equal
            (result
                .GetProperty("outcome")
                .GetProperty("data")
                .GetProperty("snapshot")
                .GetProperty("tag")
                .GetString())
            tag
            "Disclosure remains explicit"

let private previewCodecs () =
    let review: AdvisoryReview =
        {
            Before = None
            Proposed = Case.snapshot (opened ())
            Changes = []
            Context =
                {
                    ProductVersion = "0.10.0"
                    EffectiveBusinessDate = today
                    TimeZoneId = "Etc/UTC"
                }
        }

    for value in
        [
            PreviewOutcome.Reviewed(OperationReview.Case review)
            PreviewOutcome.AlreadyAccepted receipt
            PreviewOutcome.Rejected ClaimCore.Contract.Refusal.ActNotPermitted
            PreviewOutcome.Failed CoreFault.StoreIntegrityError
            PreviewOutcome.Cancelled
        ] do
        OutcomeMapping.preview id value |> admitted |> ignore

let private diagnostics () =
    for fault, _ in CoreFaults.all do
        OutcomeMapping.caseGet (ReadReply.Failed fault) |> admitted |> ignore

    for value in
        [
            ClaimCore.Contract.Refusal.LedgerExhausted
            ClaimCore.Contract.Refusal.InvalidCursor
            ClaimCore.Contract.Refusal.LastOwner
            ClaimCore.Contract.Refusal.ActNotPermitted
            ClaimCore.Contract.Refusal.ResourceUnavailable
        ] do
        let result = OutcomeMapping.caseGet (ReadReply.Rejected value) |> admitted

        Expect.equal
            (result.GetProperty("outcome").GetProperty("data").GetProperty("code").GetString())
            (Refusals.row value).Code
            "The refusal carries its stable code"

let private actionExpectations =
    [
        (Refusal.InvalidInput(InputTarget.Reason, Violation.TextRequired)),
        RecommendedAction.CorrectInput,
        "CORRECT_INPUT",
        Some "reason",
        None
        Refusal.InvalidInput(InputTarget.Limit, Violation.PageLimitRange 50),
        RecommendedAction.CorrectInput,
        "CORRECT_INPUT",
        Some "limit",
        None
        ClaimCore.Contract.Refusal.InvalidCursor,
        RecommendedAction.ReadCurrent,
        "READ_CURRENT",
        Some "cursor",
        None
        (Refusal.RevisionConflict 7L), RecommendedAction.ReadCurrent, "READ_CURRENT", None, Some 7L
    ]

let private actionEnums () =
    for refusal, expected, _, _, _ in actionExpectations do
        Expect.equal
            refusal.Action
            expected
            "Input correction and current-state recovery are distinct"

let private positiveActualRevision () =
    Expect.throws
        (fun () ->
            OutcomeMapping.caseGet (ReadReply.Rejected(Refusal.RevisionConflict 0L))
            |> Replies.encode
            |> ignore)
        "The captured baseline refuses zero actualRevision on the wire."

let private actionTokens () =
    positiveActualRevision ()

    for refusal, _, expected, field, revision in actionExpectations do
        let result = OutcomeMapping.caseGet (ReadReply.Rejected refusal) |> admitted

        let data = result.GetProperty("outcome").GetProperty("data")

        Expect.equal (data.GetProperty("action").GetString()) expected "Literal wire guidance"

        Expect.equal
            (data.GetProperty("target").GetString())
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
            "{}"
            "{\"endpoint\":\"context\",\"input\":{},\"extra\":0}"
            "{\"endpoint\":\"context\",\"endpoint\":\"context\",\"input\":{}}"
            "not-json"
        ] do
        Expect.isError
            (Frames.admit (bytes value))
            "Malformed, duplicate and unknown members refuse"

    Expect.isError (Frames.admit [| 0xffuy |]) "UTF8 is strict"

    Expect.isError
        (Frames.admit (Array.create (Limits.MaximumRequestBytes + 1) 32uy))
        "Frames are bounded"

    let original = bytes "{ \"endpoint\": \"context\", \"input\": { } }"
    let invocation = Frames.admit original |> accepted
    let copy = Frames.inputBytes invocation
    copy[0] <- 0uy

    Expect.equal
        (Frames.inputBytes invocation)
        (bytes "{ }")
        "Stored original input bytes survive caller mutation"

let private integerLexemes () =
    let wire =
        Admission.encode (WireRequest.CaseExecute(wireRequest registration))
        |> Encoding.UTF8.GetString

    for value in [ "-1"; "9007199254740992"; "1.0"; "1e0"; "\"1\"" ] do
        Expect.isError
            (Admission.admit
                Endpoint.CaseExecute
                (bytes (wire.Replace("\"expectedRevision\":0", "\"expectedRevision\":" + value))))
            "Strict lexical request integers"

    for value in [ "0"; "-0"; "9007199254740991" ] do
        Expect.isOk
            (Admission.admit
                Endpoint.CaseExecute
                (bytes (wire.Replace("\"expectedRevision\":0", "\"expectedRevision\":" + value))))
            "Safe integer boundaries"

    for sequence in [ 0L; 9007199254740992L ] do
        Expect.throws
            (fun () ->
                OutcomeMapping.execute
                    id
                    (ExecuteOutcome.Accepted
                        { receipt with
                            Position = { position with Sequence = sequence }
                        })
                |> Replies.encode
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
                View = ClaimCore.Contract.CaseView.Live(fields, Disposition.Active)
            }

        let prior = Case.ofAccepted snapshot |> accepted

        OutcomeMapping.caseGet (
            ReadReply.Succeeded
                {
                    Snapshot = snapshot
                    Advice = { Acts = []; History = [] }
                }
        )
        |> admitted
        |> ignore

        let values =
            { registration with
                ClaimantName = name
                InsurerName = "Changed insurer"
            }

        let reaffirm = request 1L (CaseAct.AmendRegistration values)

        let input =
            { wireRequest values with
                ExpectedRevision = 1L
                Act = ClaimCore.Contract.CaseAct.AmendRegistration(wireRegistration values)
            }

        Admission.admit Endpoint.CaseExecute (Admission.encode (WireRequest.CaseExecute input))
        |> accepted
        |> ignore

        Case.decide today reaffirm (Some prior) |> accepted |> ignore

        let changed =
            { reaffirm with
                Act =
                    (CaseAct.AmendRegistration
                        { values with
                            ClaimantName = name + "B"
                        })
            }

        Expect.isError
            (Case.decide today changed (Some prior))
            "New assertions follow current admission"

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

        let syntax = Admission.admit Endpoint.AuthorityExecute wire |> accepted

        Expect.equal
            (match syntax with
             | WireRequest.AuthorityExecute request ->
                 AuthorityRequests.admit request |> Result.isOk
             | _ -> false)
            admitted
            "Only authoritative principal admission judges identity semantics"

    let invalidKind = authorityWire "UNKNOWN" "https://issuer.example/" "subject"

    Expect.isError (Admission.admit Endpoint.AuthorityExecute invalidKind) "Closed principal kind"

let private artifact name =
    JsonDocument.Parse(
        File.ReadAllBytes(Path.Combine(RepositoryRoot.find (), "contracts", name)).AsMemory()
    )

let private policyHints () =
    use descriptors = artifact "descriptors.json"

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

let private authorityFieldPresentation () =
    use document = artifact "descriptors.json"
    let root = document.RootElement
    let fields = root.GetProperty("authorityFields").EnumerateArray() |> Seq.toList

    let names =
        fields
        |> List.map (fun field -> field.GetProperty("name").GetString() |> nonNull)

    Expect.equal names.Length (Set.ofList names).Count "Presentation identifiers are unique"

    Expect.equal
        (root.GetProperty("fields").GetArrayLength())
        13
        "Protocol labels add no business fields"

    let application =
        AuthorityOperationDefinitions.target.Name
        :: (AuthorityOperationDefinitions.all
            |> List.collect (fun definition -> definition.Inputs |> List.map _.Name))

    Expect.equal
        (Set.ofList names)
        (Set.ofList (
            [ "operationId"; "expectedRevision"; "act"; "kind"; "issuer"; "subject" ]
            @ application
        ))
        "Every owned technical input has presentation metadata"

    for field in fields do
        let name = field.GetProperty("name").GetString() |> nonNull
        let labelId = field.GetProperty("labelId").GetString() |> nonNull
        Expect.equal labelId ("authority.field." + name + ".label") "Stable message identifier"

        let literal =
            root.GetProperty("messages").GetProperty(labelId).EnumerateArray() |> Seq.head

        Expect.equal
            (literal.GetProperty("literal").GetString())
            (field.GetProperty("label").GetString())
            "English follows owned field metadata"

let private decoderProperty =
    let seed = Admission.encode (WireRequest.CaseExecute(wireRequest registration))
    let generator = Gen.choice [ DecoderCorpora.jsonLike; DecoderCorpora.mutated seed ]

    property {
        let! value = generator

        let total decoder =
            try
                decoder value |> ignore
                true
            with _ ->
                false

        return
            total Frames.admit
            && total Frames.decode
            && (Endpoint.all
                |> List.forall (fun endpoint ->
                    total (Admission.admit endpoint) && total (Replies.decode endpoint)))
    }

[<Tests>]
let tests =
    testList
        "service contract"
        [
            testCase
                "technical authority inputs retain owned presentation"
                authorityFieldPresentation
            testCase
                "principal syntax and authoritative identity admission remain distinct"
                principalAdmission
            testCase "text policy remains a nonvalidating presentation hint" policyHints
            testCase
                "retained text renders and reaffirms while changed policy violations refuse"
                retainedText
            testCase "both mutation scopes share outcomes and exit codes" executeOutcomes
            testCase "withheld and erased snapshots remain distinct" disclosures
            testCase "query views and context encode through typed outcomes" (fun () ->
                queryCodecs ()
                contextCodec ())
            testCase "preview encodes review and replay outcomes" previewCodecs
            testCase "closed diagnostics have presentation and no coarse codes" diagnostics
            testCase "rejections retain core recovery guidance" actionEnums
            testCase "rejections retain wire recovery guidance" actionTokens
            testCase "strict frames reject hostile shapes and preserve original bytes" rejectFrames
            testCase "wire integers enforce lexemes and safe boundaries" integerLexemes
            testCase "all frame and HTTP decoders are total for bounded hostile bytes" (fun () ->
                PropertyHarness.run "CC-FUZZ-INVOCATION-001" decoderProperty)
        ]
