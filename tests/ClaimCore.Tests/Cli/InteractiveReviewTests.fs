module ClaimCore.Tests.InteractiveReviewTests

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Cli
open ClaimCore.TestSupport

[<RequireQualifiedAccess>]
type private Scenario =
    | Accepted
    | ReadRefused
    | PreviewRefused
    | Stale

[<NoEquality; NoComparison>]
type private Harness =
    {
        Console: InteractiveConsole
        Transport: InteractiveTransport
        Calls: ResizeArray<Invocation>
        Text: ResizeArray<string>
        Prompts: ResizeArray<string>
        NewId: unit -> Guid
        Generated: unit -> int
        ReadCount: unit -> int
    }

let private identity = Guid.Parse "90000000-0000-4000-8000-000000000001"

let private current =
    { CliReplyFixtures.snapshot with
        Revision = CaseFixtures.revision 7L
    }

let private caseReply =
    {
        Mine = false
        Work = Inputs.workView current.State
        Stages = Inputs.stageDisplay current.State
        Snapshot = current
        Headline = CaseHeadline.Undecided
        Attention = []
        Advice = Inputs.caseAdvice current.State
    }

let private review =
    OperationReview.Case
        {
            Before = Some current
            Proposed =
                { current with
                    Revision = CaseFixtures.revision 8L
                    State =
                        current.State
                        |> CaseState.map (fun fields ->
                            { fields with
                                Status = CaseStatus.Closed
                            })
                }
            Changes =
                {
                    Lifecycle = None
                    Fields =
                        [
                            {
                                FieldName = BusinessField.Status
                                Before = Some(CaseFieldValue.Status CaseStatus.Opened)
                                After = Some(CaseFieldValue.Status CaseStatus.Closed)
                            }
                        ]
                }
            Context =
                {
                    ProductVersion = "synthetic"
                    Basis =
                        {
                            ObservedAt = DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero)
                            BusinessDate = DateOnly(2026, 9, 7)
                            TimeZoneId = "Etc/UTC"
                        }
                }
        }

let private harness language answer scenario =
    let calls = ResizeArray<Invocation>()
    let text = ResizeArray<string>()
    let prompts = ResizeArray<string>()
    let mutable generated = 0
    let mutable reads = 0

    let response frame (codec: ClaimCore.Json.Codec<'value>) value =
        CliReply.result frame (codec.Write value) |> Fixtures.accepted

    let exchange frame (_: CancellationToken) =
        calls.Add frame
        let operationId () = Frames.operationId frame |> Option.get

        let reply =
            match (Frames.endpoint frame).Token with
            | "context" ->
                response frame Api.context.Output (ResponseExamples.defaultReply Api.context)
            | "case.get" ->
                let acceptedOpen =
                    calls
                    |> Seq.tryPick (fun call ->
                        Frames.value Api.operationExecute call
                        |> Option.bind (function
                            | OperationInput.Case request when
                                CaseAct.kind request.Act = CaseActKind.Open
                                ->
                                Some request
                            | _ -> None))

                match acceptedOpen with
                | Some _ ->
                    Expect.equal
                        generated
                        1
                        "Post-acceptance work refresh retains the frozen OPEN identity"
                | None ->
                    Expect.equal
                        generated
                        0
                        "The automatically allocated id is not frozen before reading an existing case"

                response
                    frame
                    Api.caseGet.Output
                    (if scenario = Scenario.ReadRefused then
                         Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable)
                     else
                         Outcome.Succeeded(
                             acceptedOpen
                             |> Option.map (fun request ->
                                 { caseReply with
                                     Snapshot =
                                         { caseReply.Snapshot with
                                             CaseId =
                                                 CaseId.ofOperationId (
                                                     CaseFixtures.operationId request.OperationId
                                                 )
                                             Revision = Revision.first
                                         }
                                 })
                             |> Option.defaultValue caseReply
                         ))
            | "operation.preview" ->
                response
                    frame
                    Api.operationPreview.Output
                    (PresentedPreviewReply.ofCore
                        {
                            OperationId = operationId ()
                            Outcome =
                                if scenario = Scenario.PreviewRefused then
                                    Outcome.Rejected(
                                        Refusal.Access(AccessRefusal.NotAuthorized Capability.Close)
                                    )
                                else
                                    let supplied =
                                        match Frames.value Api.operationPreview frame, review with
                                        | Some(OperationInput.Case request),
                                          OperationReview.Case value when
                                            CaseAct.kind request.Act = CaseActKind.Open
                                            ->
                                            OperationReview.Case
                                                { value with
                                                    Before = None
                                                    Proposed =
                                                        { value.Proposed with
                                                            CaseId =
                                                                CaseId.ofOperationId (
                                                                    CaseFixtures.operationId
                                                                        request.OperationId
                                                                )
                                                            Reference = CaseFixtures.reference "x"
                                                            Revision = Revision.first
                                                        }
                                                }
                                        | _ -> review

                                    Outcome.Succeeded(Previewed.Reviewed supplied)
                        })
            | "operation.execute" ->
                let act =
                    match Frames.value Api.operationExecute frame with
                    | Some(OperationInput.Case input) -> CaseAct.kind input.Act
                    | _ -> failtest "Expected reviewed case operation"

                let opening = act = CaseActKind.Open

                let receipt =
                    { CliReplyFixtures.receipt with
                        OperationId = CaseFixtures.operationId (operationId ())
                        Subject =
                            match CliReplyFixtures.receipt.Subject with
                            | ReceiptSubject.Case subject ->
                                ReceiptSubject.Case
                                    { subject with
                                        CaseId =
                                            if opening then
                                                CaseId.ofOperationId (
                                                    CaseFixtures.operationId (operationId ())
                                                )
                                            else
                                                current.CaseId
                                        Reference = current.Reference
                                        Revision =
                                            if opening then
                                                Revision.first
                                            else
                                                CaseFixtures.revision 8L
                                        Act = act
                                    }
                            | _ -> failtest "The case receipt fixture must identify its actual case"
                    }

                response
                    frame
                    Api.operationExecute.Output
                    {
                        OperationId = operationId ()
                        Outcome =
                            if scenario = Scenario.Stale then
                                ExecuteResult.Rejected(
                                    Refusal.RevisionConflict(
                                        RevisionConflictInfo.at (CaseFixtures.revision 8L)
                                        |> RevisionConflictInfo.explain ConflictChanges.Withheld
                                    )
                                )
                            else
                                ExecuteResult.Accepted receipt
                    }
            | _ -> failtest "An interactive case flow cannot dispatch another endpoint"

        Task.FromResult reply

    {
        Console =
            {
                IsInteractive = true
                ReadLine =
                    fun () ->
                        reads <- reads + 1
                        answer
                Write = fun bytes -> prompts.Add(Encoding.UTF8.GetString bytes)
            }
        Transport =
            {
                Exchange = exchange
                Deliver =
                    fun frame reply ->
                        text.Add(
                            TextReply.bytesFor language (Some frame) reply.Frame
                            |> Encoding.UTF8.GetString
                        )
            }
        Calls = calls
        Text = text
        Prompts = prompts
        NewId =
            fun () ->
                generated <- generated + 1
                identity
        Generated = fun () -> generated
        ReadCount = fun () -> reads
    }

let private run arguments (harness: Harness) =
    let draft =
        InteractiveReview.prepare harness.Console arguments
        |> Fixtures.accepted
        |> Option.defaultWith (fun () -> failtest "Expected an interactive draft")

    InteractiveReview.run
        harness.NewId
        harness.Console
        harness.Transport
        draft
        CancellationToken.None
    |> fun value -> value.GetAwaiter().GetResult()

let private endpoints (harness: Harness) =
    harness.Calls |> Seq.map (Frames.endpoint >> _.Token) |> Seq.toList

let private caseInput frame =
    match Api.operationExecute.Input.Read "$" (Frames.input frame) |> Fixtures.accepted with
    | OperationInput.Case request -> request
    | _ -> failtest "Expected a case request"

let private unavailable console arguments =
    match InteractiveReview.prepare console arguments with
    | Ok None -> ()
    | _ -> failtest "This path must retain strict normal preparation"

[<Tests>]
let tests =
    testList
        "interactive CLI review"
        [
            testCase
                "OPEN previews without a lookup and confirms its exact authored input"
                (fun () ->
                    for answer, executed in [ Some "yes", true; Some "no", false; None, false ] do
                        let h = harness "en" answer Scenario.Accepted

                        let arguments =
                            [ "case"; "open"; "x" ]
                            @ CliExamples.registrationArguments Inputs.registration

                        Expect.equal (run arguments h) 0 "Human completion"

                        let expected =
                            [ "context"; "operation.preview" ]
                            @ (if executed then [ "operation.execute"; "case.get" ] else [])

                        Expect.equal
                            (endpoints h)
                            expected
                            "A new reference is not looked up before OPEN"

                        let input = caseInput h.Calls[1]
                        Expect.equal input.ExpectedRevision 0L "Zero is the actual OPEN contract"

                        Expect.equal
                            input.Target
                            (CaseSelector.Reference "x")
                            "Preview binds the authored reference"

                        Expect.equal
                            (h.Generated())
                            1
                            "Allocate one operation ID after valid syntax"

                        if executed then
                            Expect.equal
                                (Frames.inputBytes h.Calls[1])
                                (Frames.inputBytes h.Calls[2])
                                "The exact previewed input is executed"
                        else
                            Expect.contains
                                (List.ofSeq h.Prompts)
                                "Not recorded.\n"
                                "Explicit declined OPEN output")
            testCase
                "accept freezes one id and fetched revision across preview and execute"
                (fun () ->
                    let h = harness "en" (Some "yes") Scenario.Accepted
                    Expect.equal (run [ "case"; "close"; "x" ] h) 0 "Accepted execution"

                    Expect.equal
                        (endpoints h)
                        [ "context"; "case.get"; "operation.preview"; "operation.execute" ]
                        "Read, review, then execute"

                    Expect.equal (h.Generated()) 1 "Allocate one fresh operation id after the read"
                    Expect.equal (h.ReadCount()) 1 "Consent is requested once"
                    let pending = caseInput h.Calls[2]

                    Expect.equal
                        pending.ExpectedRevision
                        7L
                        "The server read supplies the expected revision"

                    Expect.equal
                        pending.OperationId
                        identity
                        "The fresh id belongs to the reviewed operation"

                    Expect.equal
                        (Frames.inputBytes h.Calls[2])
                        (Frames.inputBytes h.Calls[3])
                        "Confirmation executes exactly the reviewed input")
            testCase "erasure requires the authored reference rather than yes" (fun () ->
                for answer, executed in
                    [ Some "yes", false; Some "x ", false; None, false; Some "x", true ] do
                    let h = harness "en" answer Scenario.Accepted
                    Expect.equal (run [ "case"; "erase"; "x" ] h) 0 "Human completion"

                    Expect.equal
                        (endpoints h |> List.contains "operation.execute")
                        executed
                        "The irreversible write is sent only after exact target entry"

                    Expect.stringContains
                        (String.concat "" h.Prompts)
                        "Type x to confirm: "
                        "Typed target prompt")
            testCase "decline never dispatches execute" (fun () ->
                let h = harness "en" (Some "n") Scenario.Accepted

                Expect.equal
                    (run [ "case"; "close"; "x" ] h)
                    0
                    "Decline is a completed human choice"

                Expect.equal
                    (endpoints h)
                    [ "context"; "case.get"; "operation.preview" ]
                    "No mutation follows decline"

                Expect.equal
                    (h.Prompts |> Seq.last)
                    "Not recorded.\n"
                    "Decline gives explicit closure"

                Expect.isFalse
                    (h.Text |> Seq.exists (fun value -> value.Contains "Active → Active"))
                    "Unchanged lifecycle values do not obscure the actual changes")
            testCase "empty or closed confirmation input defaults to no" (fun () ->
                for answer in [ None; Some ""; Some "unexpected" ] do
                    let h = harness "en" answer Scenario.Accepted
                    run [ "case"; "close"; "x" ] h |> ignore
                    Expect.equal h.Calls.Count 3 "Only an explicit affirmative answer can execute")
            testCase "a stale revision stays a definite refusal without retry" (fun () ->
                let h = harness "en" (Some "y") Scenario.Stale

                Expect.equal
                    (run [ "case"; "close"; "x" ] h)
                    2
                    "The service still enforces its locked revision"

                Expect.equal
                    h.Calls.Count
                    4
                    "The client does not fetch a new revision and bypass consent"

                Expect.equal
                    (caseInput h.Calls[3]).ExpectedRevision
                    7L
                    "Execute retains the reviewed revision")
            testCase "a refused read stops before id allocation preview or confirmation" (fun () ->
                let h = harness "en" (Some "y") Scenario.ReadRefused
                Expect.equal (run [ "case"; "close"; "x" ] h) 2 "Read refusal is preserved"

                Expect.equal
                    (endpoints h)
                    [ "context"; "case.get" ]
                    "No further operation is attempted"

                Expect.equal (h.Generated()) 0 "No reviewed operation was created"
                Expect.equal (h.ReadCount()) 0 "A refusal cannot invite consent")
            testCase "a refused preview stops before confirmation or execute" (fun () ->
                let h = harness "en" (Some "y") Scenario.PreviewRefused
                Expect.equal (run [ "case"; "close"; "x" ] h) 2 "Preview refusal is preserved"
                Expect.equal h.Calls.Count 3 "No execute follows a refused review"
                Expect.equal (h.ReadCount()) 0 "No confirmation follows refusal")
            testCase "nonterminal omission remains a usage error" (fun () ->
                let h = harness "en" (Some "y") Scenario.Accepted
                unavailable { h.Console with IsInteractive = false } [ "case"; "close"; "x" ]

                match Grammar.prepare (fun () -> string identity) [ "case"; "close"; "x" ] with
                | Error issues ->
                    Expect.contains
                        issues
                        (UsageError.MissingOption "--expected-revision")
                        "Automation must supply a revision"
                | Ok _ -> failtest "Omission cannot become an executable invocation"

                Expect.equal h.Calls.Count 0 "No remote call was made")
            testCase "JSON and CSV formats never enter interactive review" (fun () ->
                let h = harness "en" (Some "y") Scenario.Accepted

                for format in [ "json"; "csv" ] do
                    unavailable h.Console [ "case"; "close"; "x"; "--format"; format ]
                    unavailable h.Console [ "case"; "close"; "x"; "--format=" + format ]

                Expect.isError
                    (Grammar.prepare
                        (fun () -> string identity)
                        [ "case"; "close"; "x"; "--format"; "json" ])
                    "JSON omission remains usage-invalid"

                Expect.equal h.Calls.Count 0 "No remote call was made")
            testCase
                "omitted correction groups mean KEEP and one unchanged summary replaces repeated KEEP lines"
                (fun () ->
                    let h = harness "en" (Some "n") Scenario.Accepted
                    run [ "case"; "correct"; "x" ] h |> ignore

                    match (caseInput h.Calls[2]).Act with
                    | CaseAct.Correct correction ->
                        Expect.equal
                            correction.Registration
                            Amendment.Keep
                            "Registration is retained"

                        Expect.equal correction.Decision Change.Keep "Decision is retained"
                        Expect.equal correction.Payment PaymentChange.Keep "Payment is retained"
                    | _ -> failtest "Expected a correction review"

                    let rendered = String.concat "\n" h.Text

                    Expect.equal
                        (rendered.Split('\n')
                         |> Array.filter (fun line ->
                             line.StartsWith("Unchanged:", StringComparison.Ordinal))
                         |> Array.length)
                        1
                        "One summary covers unchanged values"

                    Expect.isFalse
                        (rendered.Contains "Keep current details")
                        "Unchanged group actions do not obscure the supplied changes")
            testCase "localized prompts accept affirmative Latvian and Arabic replies" (fun () ->
                for language, answer, prompt in
                    [
                        "lv", "JĀ", "Reģistrēt šīs izmaiņas? [j/N] "
                        "ar", "نعم", "هل تريد تسجيل هذه التغييرات؟ [نعم/لا] "
                    ] do
                    let h = harness language (Some answer) Scenario.Accepted

                    Expect.equal
                        (run [ "case"; "close"; "x"; "--language=" + language ] h)
                        0
                        "Localized confirmation"

                    Expect.equal
                        (List.ofSeq h.Prompts)
                        [ prompt ]
                        "A real localized prompt is displayed"

                    Expect.equal
                        (CliInvocations.usageLanguage [ "case"; "close"; "--language=" + language ])
                        language
                        "Assigned language options also localize usage errors")
            testCase "new language confirmations require a localized affirmative" (fun () ->
                for language, answer in
                    [
                        "ko", "예"
                        "ja", "はい"
                        "zh-Hans", "确认"
                        "zh-Hant", "確認"
                        "es", "SÍ"
                        "de", "JA"
                    ] do
                    Expect.isTrue
                        (CliConfirmation.confirmed language (Some answer))
                        "Explicit localized consent"

                    for refusal in [ None; Some ""; Some "no"; Some "否"; Some "not yes" ] do
                        Expect.isFalse
                            (CliConfirmation.confirmed language refusal)
                            "Missing or negative answers cannot dispatch")
            testCase "explicit preview remains preview-only without asking to execute" (fun () ->
                let h = harness "en" (Some "y") Scenario.Accepted
                Expect.equal (run [ "case"; "close"; "x"; "--preview" ] h) 0 "Preview completed"
                Expect.equal h.Calls.Count 3 "Preview does not execute"
                Expect.equal (h.ReadCount()) 0 "Preview does not request mutation consent")
            testCase "offline dry-run and explicit revisions keep their normal paths" (fun () ->
                let h = harness "en" (Some "y") Scenario.Accepted
                unavailable h.Console [ "case"; "close"; "x"; "--dry-run" ]
                unavailable h.Console [ "case"; "close"; "x"; "--expected-revision"; "7" ]

                Expect.isError
                    (Grammar.prepare
                        (fun () -> string identity)
                        [ "case"; "close"; "x"; "--dry-run" ])
                    "An offline frame needs an explicit revision"

                Expect.equal h.Calls.Count 0 "No remote call was made")
            testCase "an explicit operation id is retained across the reviewed attempt" (fun () ->
                let h = harness "en" (Some "y") Scenario.Accepted
                let supplied = Guid.Parse "80000000-0000-4000-8000-000000000099"
                run [ "case"; "close"; "x"; "--operation-id"; string supplied ] h |> ignore
                Expect.equal (h.Generated()) 0 "An explicit id is not silently replaced"

                Expect.equal
                    (caseInput h.Calls[2]).OperationId
                    supplied
                    "Preview retains the explicit id"

                Expect.equal
                    (Frames.inputBytes h.Calls[2])
                    (Frames.inputBytes h.Calls[3])
                    "Execute retains the same explicit id")
        ]
