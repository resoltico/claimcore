module ClaimCore.Tests.ContractTests

open ClaimCore.Cli

open ClaimCore.Protocol
open ClaimCore.Json

open System
open System.Text
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Application
open ClaimCore.TestSupport
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let private bytes (value: string) = Encoding.UTF8.GetBytes value
let private id = Guid.Parse "20000000-0000-4000-8000-000000000001"
let private digest = String.replicate 64 "a"

let private position: ClaimCore.Domain.LedgerPosition =
    {
        Sequence = 1L
        EntryHash = (Convert.FromHexString digest |> EntryHash.ofBytes |> Option.get)
    }

let private receipt: ClaimCore.Domain.OperationReceipt =
    {
        OperationId = id
        Position = position
        Subject =
            ClaimCore.Domain.ReceiptSubject.Case
                {
                    Reference = CaseFixtures.reference "UNIT-001"
                    Revision = 1L
                    Act = CaseActKind.Open
                    Snapshot =
                        ClaimCore.Domain.SnapshotDisclosure.Disclosed(
                            LiveCase.ofState (Case.snapshot (opened ())).State |> Option.get
                        )
                }
        RecordedAt = DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero)
        RecordedBy = id
    }

let private authority =
    { receipt with
        Subject =
            ClaimCore.Domain.ReceiptSubject.Authority
                {
                    TargetActorId = id
                    Act = AuthorityKind.Register
                }
    }

let private admitted codec value =
    let encoded = Codec.encode codec value

    Expect.equal
        (Codec.decode Limits.MaximumResponseBytes codec encoded)
        (Ok value)
        "Typed reply round trips"

    use document = JsonDocument.Parse(encoded.AsMemory())
    document.RootElement.Clone()

let private caseRequest values : ClaimCore.Domain.UnvalidatedCaseRequest =
    {
        OperationId = id
        CaseReference = "UNIT-001"
        ExpectedRevision = 0L
        Act = ClaimCore.Domain.AuthoredCaseAct.Open values
    }

let private contextCodec () =
    admitted
        Api.context.Output
        (Outcome.Succeeded
            {
                Runtime =
                    {
                        ProductVersion = "0.10.0"
                        EffectiveBusinessDate = today
                        TimeZoneId = "Etc/UTC"
                    }
                Capability = []
                View = []
                Offers = []
            })

    |> ignore

let private queryCodecs () =
    let snapshot = Case.snapshot (opened ())

    for view in
        [
            snapshot
            { snapshot with
                State = ClaimCore.Domain.CaseState.Erased
            }
        ] do
        admitted
            Api.caseGet.Output
            (Outcome.Succeeded
                {
                    Snapshot = view
                    Advice = (CaseAdvice.create [] None [] |> Result.defaultWith invalidOp)
                })

        |> ignore

    admitted
        Api.caseList.Output
        (Outcome.Succeeded
            {
                Items = []
                NextCursor = None
                Offers = []
            })

    |> ignore

    admitted Api.caseHistory.Output (Outcome.Succeeded { Entries = []; NextCursor = None })

    |> ignore

    admitted Api.operationObserve.Output (Outcome.Succeeded authority)

    |> ignore

    admitted Api.authorityList.Output (Outcome.Succeeded { Items = []; NextCursor = None })

    |> ignore


let private executeOutcomes () =
    for receiptValue in [ receipt; authority ] do
        for outcome, exit in
            [
                ExecuteResult.Accepted receiptValue, 0
                ExecuteResult.AlreadyAccepted receiptValue, 0
                ExecuteResult.Rejected Refusal.ActNotPermitted, 2
                ExecuteResult.Failed CoreFault.StoreUnavailable, 3
                ExecuteResult.OutcomeUnknown, 4
            ] do
            let reply: ExecuteReply =
                {

                    OperationId = id
                    Outcome = outcome
                }

            admitted Api.operationExecute.Output reply |> ignore

            let frame: ClaimCore.Cli.FrameReply =
                {
                    Endpoint =
                        Some(EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Execute))
                    OperationId = Some id
                    Delivery =
                        if exit = 4 then
                            DeliveryState.Uncertain
                        else
                            DeliveryState.Delivered
                    Result =
                        FrameResult.Reply(
                            ClaimCore.Tests.CliReplies.ofValue Api.operationExecute reply
                        )
                }

            Expect.equal
                (ClaimCore.Cli.Frames.decode (ClaimCore.Cli.Frames.encode frame))
                (Ok frame)
                "CLI frames share mutation outcomes"

            Expect.equal
                (ClaimCore.Cli.Frames.exitCode frame)
                exit
                "Mutation scope preserves exit semantics"

let private disclosures () =
    for snapshot, tag in
        [ SnapshotDisclosure.Withheld, "WITHHELD"; SnapshotDisclosure.Erased, "ERASED" ] do
        let value =
            { receipt with
                Subject =
                    ClaimCore.Domain.ReceiptSubject.Case
                        {
                            Reference = CaseFixtures.reference "UNIT-001"
                            Revision = 1L
                            Act = CaseActKind.Open
                            Snapshot = snapshot
                        }
            }

        let result = admitted Api.operationObserve.Output (Outcome.Succeeded value)

        Expect.equal
            (result
                .GetProperty("outcome")
                .GetProperty("data")
                .GetProperty("subject")
                .GetProperty("snapshot")
                .GetProperty("kind")
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
            (Previewed.Reviewed >> Outcome.Succeeded) (OperationReview.Case review)
            (Previewed.AlreadyAccepted >> Outcome.Succeeded) receipt
            Outcome.Rejected ClaimCore.Domain.Refusal.ActNotPermitted
            Outcome.Failed CoreFault.StoreIntegrityError
        ] do
        admitted Api.operationPreview.Output { OperationId = id; Outcome = value }
        |> ignore

let private diagnostics () =
    for fault, _ in CoreFault.all do
        admitted Api.caseGet.Output (Outcome.Failed fault) |> ignore

    for value, expectedCode in
        [
            ClaimCore.Domain.Refusal.InvalidCursor, "INVALID_CURSOR"
            ClaimCore.Domain.Refusal.LastOwner, "LAST_OWNER"
            ClaimCore.Domain.Refusal.ActNotPermitted, "ACT_NOT_PERMITTED"
            ClaimCore.Domain.Refusal.ResourceUnavailable, "RESOURCE_UNAVAILABLE"
        ] do
        let result = admitted Api.caseGet.Output (Outcome.Rejected value)

        Expect.equal
            (result.GetProperty("outcome").GetProperty("data").GetProperty("code").GetString())
            expectedCode
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
        ClaimCore.Domain.Refusal.InvalidCursor,
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

let private diagnosticRevisions () =
    let reason = Refusal.RevisionConflict 0L
    let codec = DiagnosticCodecs.diagnostic

    Expect.equal
        (Codec.encode codec reason |> Codec.decode 10000 codec)
        (Ok reason)
        "An absent record can report actual revision zero"

    Expect.throws
        (fun () -> Codec.encode codec (Refusal.RevisionConflict -1L) |> ignore)
        "Negative actual revisions remain invalid"

    let snapshot =
        { Case.snapshot (opened ()) with
            Revision = 0L
        }

    Expect.throws
        (fun () -> Codec.encode ResponseCodecs.caseSnapshot snapshot |> ignore)
        "An accepted snapshot revision stays positive"

    let invalidReceipt =
        match receipt.Subject with
        | ReceiptSubject.Case target ->
            { receipt with
                Subject = ReceiptSubject.Case { target with Revision = 0L }
            }
        | _ -> failtest "Case receipt fixture"

    Expect.throws
        (fun () -> Codec.encode ReceiptCodecs.receipt invalidReceipt |> ignore)
        "An accepted receipt revision stays positive"

let private actionTokens () =
    diagnosticRevisions ()

    for refusal, _, expected, field, revision in actionExpectations do
        let result = admitted Api.caseGet.Output (Outcome.Rejected refusal)

        let data = result.GetProperty("outcome").GetProperty("data")

        Expect.equal (data.GetProperty("action").GetString()) expected "Literal wire guidance"

        let mutable encodedTarget = Unchecked.defaultof<JsonElement>
        let hasTarget = data.TryGetProperty("target", &encodedTarget)
        Expect.equal hasTarget field.IsSome "Only applicable target metadata is serialized"

        field
        |> Option.iter (fun target ->
            Expect.equal (encodedTarget.GetString()) target "Literal target")

        let mutable encodedRevision = Unchecked.defaultof<JsonElement>
        let hasRevision = data.TryGetProperty("actualRevision", &encodedRevision)
        Expect.equal hasRevision revision.IsSome "Only revision conflicts carry revision metadata"

        revision
        |> Option.iter (fun revision ->
            Expect.equal (encodedRevision.GetInt64()) revision "Literal revision")

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
            (ClaimCore.Cli.Frames.admit (bytes value))
            "Malformed, duplicate and unknown members refuse"

    Expect.isError (ClaimCore.Cli.Frames.admit [| 0xffuy |]) "UTF8 is strict"

    Expect.isError
        (ClaimCore.Cli.Frames.admit (Array.create (Limits.MaximumInvocationBytes + 1) 32uy))
        "Frames are bounded"

    let original = bytes "{ \"endpoint\": \"context\", \"input\": { } }"
    let invocation = ClaimCore.Cli.Frames.admit original |> accepted
    let copy = ClaimCore.Cli.Frames.inputBytes invocation
    copy[0] <- 0uy

    Expect.equal
        (ClaimCore.Cli.Frames.inputBytes invocation)
        (bytes "{}")
        "Stored prepared input bytes survive caller mutation"

let private integerLexemes () =
    let wire =
        Codec.encode Api.operationExecute.Input (OperationInput.Case(caseRequest registration))
        |> Encoding.UTF8.GetString

    for value in [ "-1"; "9007199254740992"; "1.0"; "1e0"; "\"1\"" ] do
        Expect.isError
            (Codec.decode
                Limits.MaximumRequestBytes
                Api.operationExecute.Input
                (bytes (wire.Replace("\"expectedRevision\":0", "\"expectedRevision\":" + value))))
            "Strict lexical request integers"

    for value in [ "0"; "-0"; "9007199254740991" ] do
        Expect.isOk
            (Codec.decode
                Limits.MaximumRequestBytes
                Api.operationExecute.Input
                (bytes (wire.Replace("\"expectedRevision\":0", "\"expectedRevision\":" + value))))
            "Safe integer boundaries"

    for sequence in [ 0L; 9007199254740992L ] do
        Expect.throws
            (fun () ->
                Codec.encode
                    Api.operationExecute.Output
                    {

                        OperationId = id
                        Outcome =
                            ExecuteResult.Accepted
                                { receipt with
                                    Position = { position with Sequence = sequence }
                                }
                    }
                |> ignore)
            "Accepted sequence bounds"

let private retainedText () =
    for name in [ String.replicate 201 "A"; "Earlier‮Name" ] do
        let original = Case.snapshot (opened ())

        let fields =
            { viewFields original with
                Registration =
                    { (viewFields original).Registration with
                        ClaimantName = CaseFixtures.name name
                    }
            }

        let snapshot =
            { original with
                State = ClaimCore.Domain.CaseState.Active(fields)
            }

        let prior = Case.ofAccepted snapshot |> accepted

        admitted
            Api.caseGet.Output
            (Outcome.Succeeded
                {
                    Snapshot = snapshot
                    Advice = (CaseAdvice.create [] None [] |> Result.defaultWith invalidOp)
                })

        |> ignore

        let values =
            { registration with
                ClaimantName = name
                InsurerName = "Changed insurer"
            }

        let reaffirm = request 1L (AuthoredCaseAct.AmendRegistration values)

        let input =
            { caseRequest values with
                ExpectedRevision = 1L
                Act = ClaimCore.Domain.AuthoredCaseAct.AmendRegistration values
            }

        Codec.decode
            Limits.MaximumRequestBytes
            Api.operationExecute.Input
            (Codec.encode Api.operationExecute.Input (OperationInput.Case input))
        |> accepted
        |> ignore

        CaseFixtures.decide today reaffirm (Some prior) |> accepted |> ignore

        let changed =
            { reaffirm with
                Act =
                    (AuthoredCaseAct.AmendRegistration
                        { values with
                            ClaimantName = name + "B"
                        })
            }

        Expect.isError
            (CaseFixtures.decide today changed (Some prior))
            "New assertions follow current admission"

let private authorityWire kind issuer subject =
    JsonSerializer.SerializeToUtf8Bytes
        {|
            kind = "AUTHORITY"
            operationId = id.ToString("D")
            target =
                {|
                    kind = kind
                    issuer = issuer
                    subject = subject
                |}
            act = {| kind = "REGISTER" |}
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
            false, "HUMAN", "https://issuer.example/", " "
            true, "HUMAN", "https://issuer.example/", "Unicode \U0001f642 subject"
        ]

    for admitted, kind, issuer, subject in expected do
        let wire = authorityWire kind issuer subject

        let syntax =
            Codec.decode Limits.MaximumRequestBytes Api.operationExecute.Input wire
            |> accepted

        Expect.equal
            (match syntax with
             | OperationInput.Authority request -> Authority.parse request |> Result.isOk
             | _ -> false)
            admitted
            "Only authoritative principal admission judges identity semantics"

    let invalidKind = authorityWire "UNKNOWN" "https://issuer.example/" "subject"

    Expect.isError
        (Codec.decode Limits.MaximumRequestBytes Api.operationExecute.Input invalidKind)
        "Closed principal kind"

let private artifact name =
    JsonDocument.Parse(
        File.ReadAllBytes(Path.Combine(RepositoryRoot.find (), "contracts", name)).AsMemory()
    )

let private policyHints () =
    use catalog = artifact "catalog.json"

    let claimant =
        catalog.RootElement.GetProperty("fields").EnumerateArray()
        |> Seq.find (fun field -> field.GetProperty("name").GetString() = "claimantName")

    Expect.equal
        (claimant.GetProperty("scalar").GetProperty("maximumCharacters").GetInt32())
        200
        "Current policy is a nonvalidating hint"

let private authorityFieldPresentation () =
    use document = artifact "catalog.json"
    let root = document.RootElement
    let fields = root.GetProperty("inputFields").EnumerateArray() |> Seq.toList

    let names =
        fields
        |> List.map (fun field -> field.GetProperty("name").GetString() |> nonNull)

    Expect.equal names.Length (Set.ofList names).Count "Presentation identifiers are unique"

    Expect.equal
        (root.GetProperty("fields").GetArrayLength())
        13
        "Protocol labels add no business fields"

    Expect.equal
        (Set.ofList names)
        (Set.ofList
            [
                "operationId"
                "expectedRevision"
                "principal.kind"
                "principal.issuer"
                "principal.subject"
                "reason"
                "role"
                "cursor"
                "limit"
                "scope"
                "actorId"
                "detail"
            ])
        "Every catalog-owned technical input has presentation metadata"

    for field in fields do
        let name = field.GetProperty("name").GetString() |> nonNull
        let labelId = field.GetProperty("labelId").GetString() |> nonNull

        let identity =
            InteractionInput.technical
            |> List.find (fun input -> InteractionInput.key input = name)

        let prefix =
            match identity with
            | InteractionInput.Target _ -> "field."
            | InteractionInput.Scope
            | InteractionInput.ActorId
            | InteractionInput.Detail -> "input."

        Expect.equal
            labelId
            (prefix + name + ".label")
            "The input owner determines its message namespace"

        Expect.isFalse
            (field.TryGetProperty "label" |> fst)
            "CatalogDocument carry keys, not authored prose."

let private decoderProperty =
    let seed =
        Codec.encode Api.operationExecute.Input (OperationInput.Case(caseRequest registration))

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
            total ClaimCore.Cli.Frames.admit
            && total ClaimCore.Cli.Frames.decode
            && (Api.all
                |> List.forall (fun endpoint ->
                    total (fun bytes ->
                        StrictJson.read Limits.MaximumRequestBytes bytes
                        |> Result.bind (Api.admitInput endpoint))
                    && total (fun bytes ->
                        StrictJson.read Limits.MaximumResponseBytes bytes
                        |> Result.bind (Api.admitOutput endpoint))))
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
                PropertyHarness.run "invocation byte decoder" decoderProperty)
        ]
