module ClaimCore.Tests.InteractiveReviewTests

open ClaimCore.CommandLine

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
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

let private identity = Guid.Parse "90000000-0000-7000-8000-000000000001"

let private current =
    { CliReplyFixtures.snapshot with
        Revision = CaseFixtures.revision 7L
    }

let private caseReply =
    {
        Parties = []
        PartyOffers = []
        RelatedCases = RelatedCaseLinks.empty
        FamilySequence = Sequence.create 1L |> Option.get
        LatestPayments = []
        PaymentPayees = []
        Mine = false
        Work = Inputs.workView current.State
        Disposition = None
        NextActions = []
        Notes =
            {
                Items = []
                Count = 0L
                CanAdd = false
            }
        ChangedAt = DomainInputs.workBasis.ObservedAt
        RecordedBy = ClaimCore.TestSupport.ResponseExamples.caseRead.RecordedBy
        Stages = DomainInputs.stageDisplay current.State
        Snapshot = current
        Headline = CaseHeadline.Undecided
        Attention = []
        Advice =
            let offers =
                [
                    (TaskId.ofLiteral "case.close")
                    (TaskId.ofLiteral "case.erase")
                    (TaskId.ofLiteral "case.correct")
                ]

            let blocked =
                ClaimCore.Domain.Tasks.Registry.clientDeclarations
                |> List.filter (fun declaration -> declaration.Target = ActSubject.Case)
                |> List.map _.Id
                |> List.filter (fun kind -> not (List.contains kind offers))
                |> List.map (fun kind ->
                    {
                        CaseTaskRefusal.Act = kind
                        Reason = Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Undecided)
                    })

            CaseAdvice.create
                offers
                (Some
                    {
                        Registration = [ CorrectionGroupAction.Keep; CorrectionGroupAction.Update ]
                        Decision =
                            [
                                CorrectionGroupAction.Keep
                                CorrectionGroupAction.Update
                                CorrectionGroupAction.Clear
                            ]
                    })
                HistoryAccess.None
                blocked
            |> Result.defaultWith invalidOp
            |> CaseAdvice.withOffers (
                offers
                |> List.map (fun task ->
                    let binding =
                        if task = TaskId.ofLiteral "case.erase" then
                            Binding.CaseErase(
                                CaseSelector.Id current.CaseId,
                                current.Revision,
                                Sequence.first
                            )
                        else
                            Binding.Case(CaseSelector.Id current.CaseId, current.Revision)

                    let seeds =
                        if task = TaskId.ofLiteral "case.correct" then
                            [
                                TaskInputId.Target(InputTarget.Group RecordGroup.Registration),
                                InputValue.Text "KEEP"
                                TaskInputId.Target(InputTarget.Group RecordGroup.Decision),
                                InputValue.Text "KEEP"
                            ]
                            |> Map.ofList
                        else
                            Map.empty

                    Inputs.offeredTask (TaskId.token task) binding seeds)
            )
    }

let private review =
    let after =
        { current with
            Revision = CaseFixtures.revision 8L
            State =
                current.State
                |> CaseState.map (fun fields ->
                    { fields with
                        Handling =
                            Handling.Closed
                                {
                                    Outcome = ClosureOutcome.Settled
                                    Note = None
                                }
                    })
        }

    Reviews.caseReview
        (TaskId.ofLiteral "case.close")
        (Binding.Case(CaseSelector.Id current.CaseId, current.Revision))
        (Some current)
        after
        {
            ProductVersion = "synthetic"
            Basis = DomainInputs.workBasis
        }

let private harness language answer scenario =
    let calls = ResizeArray<Invocation>()
    let text = ResizeArray<string>()
    let prompts = ResizeArray<string>()
    let mutable generated = 0
    let mutable reads = 0

    let response frame (codec: ClaimCore.Json.Codec<'value>) value =
        CliReply.result frame (codec.Write value) |> CaseBuilders.accepted

    let exchange frame (_: CancellationToken) =
        calls.Add frame
        let operationId () = Frames.operationId frame |> Option.get

        let reply =
            match (Frames.endpoint frame).Token with
            | "context" ->
                response frame Api.context.Output (ResponseExamples.defaultHuman Api.context)
            | "case.get" ->
                let acceptedOpen =
                    calls
                    |> Seq.tryPick (fun call ->
                        (Frames.value Api.operationExecute) call
                        |> Option.filter (fun request ->
                            request.Task = TaskId.ofLiteral "case.open"))

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
                    ((if scenario = Scenario.ReadRefused then
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
                     |> HumanReplies.read HumanCaseDocument.ofCore
                     |> RepresentationReply.Human)
            | "operation.preview" ->
                response
                    frame
                    Api.operationPreview.Output
                    (RepresentationReply.Human(
                        HumanReplies.previewSubmission
                            ((Frames.value Api.operationPreview) frame |> Option.get)
                            {
                                OperationId = operationId ()
                                Outcome =
                                    if scenario = Scenario.PreviewRefused then
                                        Outcome.Rejected(
                                            Refusal.Access(
                                                AccessRefusal.NotAuthorized Capability.Close
                                            )
                                        )
                                    else
                                        let request =
                                            Frames.value Api.operationPreview frame |> Option.get

                                        let supplied =
                                            if request.Task = TaskId.ofLiteral "case.open" then
                                                let after =
                                                    { current with
                                                        CaseId =
                                                            CaseId.ofOperationId (
                                                                CaseFixtures.operationId
                                                                    request.OperationId
                                                            )
                                                        Reference =
                                                            CaseFixtures.reference
                                                                "CC-5NRH-CGNQ-4TR4-80B2-30"
                                                        Revision = Revision.first
                                                    }

                                                Reviews.caseReview
                                                    request.Task
                                                    (Binding.CaseNew Revision.zero)
                                                    None
                                                    after
                                                    review.Context
                                            else
                                                let after =
                                                    if
                                                        request.Task = TaskId.ofLiteral "case.erase"
                                                    then
                                                        { current with
                                                            Revision = CaseFixtures.revision 8L
                                                            State = CaseState.Erased
                                                        }
                                                    elif
                                                        request.Task =
                                                            TaskId.ofLiteral "case.correct"
                                                    then
                                                        { current with
                                                            Revision = CaseFixtures.revision 8L
                                                        }
                                                    else
                                                        { current with
                                                            Revision = CaseFixtures.revision 8L
                                                            State =
                                                                current.State
                                                                |> CaseState.map (fun fields ->
                                                                    { fields with
                                                                        Handling =
                                                                            Handling.Closed
                                                                                {
                                                                                    Outcome =
                                                                                        ClosureOutcome
                                                                                            .Settled
                                                                                    Note = None
                                                                                }
                                                                    })
                                                        }

                                                Reviews.caseReview
                                                    request.Task
                                                    (Submissions.command request).Binding
                                                    (Some current)
                                                    after
                                                    review.Context

                                        Outcome.Succeeded(Previewed.Reviewed supplied)
                            }
                    ))
            | "operation.execute" ->
                let act = (Frames.value Api.operationExecute frame |> Option.get).Task

                let opening = act = (TaskId.ofLiteral "case.open")

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
                                        Snapshot =
                                            if opening then
                                                (match ResponseExamples.receipt.Subject with
                                                 | ReceiptSubject.Case value -> value.Snapshot
                                                 | _ -> failtest "Case fixture")
                                            else
                                                subject.Snapshot
                                    }
                            | _ -> failtest "The case receipt fixture must identify its actual case"
                    }

                response
                    frame
                    Api.operationExecute.Output
                    (RepresentationReply.Human(
                        HumanReplies.execute
                            {
                                OperationId = operationId ()
                                Outcome =
                                    if scenario = Scenario.Stale then
                                        ExecuteResult.Rejected(
                                            Refusal.RevisionConflict(
                                                RevisionConflictInfo.at (CaseFixtures.revision 8L)
                                                |> RevisionConflictInfo.explain
                                                    ConflictChanges.Withheld
                                            )
                                        )
                                    else
                                        ExecuteResult.Accepted(receipt)
                            }
                    ))
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
                            ReplyText.bytesFor language (Some frame) reply.Frame
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
        |> CaseBuilders.accepted
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
    Api.operationExecute.Input.Read "$" (Frames.input frame)
    |> CaseBuilders.accepted

let private reviewedRevision (input: OperationSubmission) =
    match (Submissions.command input).Binding with
    | Binding.CaseNew version
    | Binding.Case(_, version)
    | Binding.CaseErase(_, version, _) -> Revision.value version
    | _ -> failtest "Expected reviewed case version"

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
                        let h =
                            harness (RenderingContext.forLanguage "en") answer Scenario.Accepted

                        let arguments =
                            [ "case"; "open" ]
                            @ CliExamples.registrationArguments DomainInputs.registration

                        Expect.equal (run arguments h) 0 "Human completion"

                        let expected =
                            [ "context"; "operation.preview" ]
                            @ (if executed then [ "operation.execute"; "case.get" ] else [])

                        Expect.equal
                            (endpoints h)
                            expected
                            "A new reference is not looked up before OPEN"

                        let input = caseInput h.Calls[1]
                        Expect.equal (reviewedRevision input) 0L "Zero is the actual OPEN contract"

                        Expect.isFalse
                            (Map.containsKey
                                (Submissions.field BusinessField.CaseReference)
                                (Submissions.command input).Inputs.Values)
                            "The review allocates no permanent reference"

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
                    let h =
                        harness (RenderingContext.forLanguage "en") (Some "yes") Scenario.Accepted

                    Expect.equal
                        (run
                            [ "case"; "close"; "--outcome"; "other"; "CC-5NRH-CGNQ-4TR4-80B2-30" ]
                            h)
                        0
                        "Accepted execution"

                    Expect.equal
                        (endpoints h)
                        [ "context"; "case.get"; "operation.preview"; "operation.execute" ]
                        "Read, review, then execute"

                    Expect.equal (h.Generated()) 1 "Allocate one fresh operation id after the read"
                    Expect.equal (h.ReadCount()) 1 "Consent is requested once"
                    let pending = caseInput h.Calls[2]

                    Expect.equal
                        (reviewedRevision pending)
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
            testCase "erasure requires the issued reference rather than yes" (fun () ->
                for answer, executed in
                    [
                        Some "yes", false
                        Some "CC-5NRH-CGNQ-4TR4-80B2-30 ", false
                        None, false
                        Some "CC-5NRH-CGNQ-4TR4-80B2-30", true
                    ] do
                    let h = harness (RenderingContext.forLanguage "en") answer Scenario.Accepted

                    Expect.equal
                        (run [ "case"; "erase"; "CC-5NRH-CGNQ-4TR4-80B2-30" ] h)
                        0
                        "Human completion"

                    Expect.equal
                        (endpoints h |> List.contains "operation.execute")
                        executed
                        "The irreversible write is sent only after exact target entry"

                    Expect.stringContains
                        (String.concat "" h.Prompts)
                        "Type CC-5NRH-CGNQ-4TR4-80B2-30 to confirm: "
                        "Typed target prompt")
            testCase "decline never dispatches execute" (fun () ->
                let h = harness (RenderingContext.forLanguage "en") (Some "n") Scenario.Accepted

                Expect.equal
                    (run [ "case"; "close"; "--outcome"; "other"; "CC-5NRH-CGNQ-4TR4-80B2-30" ] h)
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
                    let h = harness (RenderingContext.forLanguage "en") answer Scenario.Accepted

                    run [ "case"; "close"; "--outcome"; "other"; "CC-5NRH-CGNQ-4TR4-80B2-30" ] h
                    |> ignore

                    Expect.equal h.Calls.Count 3 "Only an explicit affirmative answer can execute")
            testCase "a stale revision stays a definite refusal without retry" (fun () ->
                let h = harness (RenderingContext.forLanguage "en") (Some "y") Scenario.Stale

                Expect.equal
                    (run [ "case"; "close"; "--outcome"; "other"; "CC-5NRH-CGNQ-4TR4-80B2-30" ] h)
                    2
                    "The service still enforces its locked revision"

                Expect.equal
                    h.Calls.Count
                    4
                    "The client does not fetch a new revision and bypass consent"

                Expect.equal
                    (reviewedRevision (caseInput h.Calls[3]))
                    7L
                    "Execute retains the reviewed revision")
            testCase "a refused read stops before id allocation preview or confirmation" (fun () ->
                let h = harness (RenderingContext.forLanguage "en") (Some "y") Scenario.ReadRefused

                Expect.equal
                    (run [ "case"; "close"; "--outcome"; "other"; "CC-5NRH-CGNQ-4TR4-80B2-30" ] h)
                    2
                    "Read refusal is preserved"

                Expect.equal
                    (endpoints h)
                    [ "context"; "case.get" ]
                    "No further operation is attempted"

                Expect.equal (h.Generated()) 0 "No reviewed operation was created"
                Expect.equal (h.ReadCount()) 0 "A refusal cannot invite consent")
            testCase "a refused preview stops before confirmation or execute" (fun () ->
                let h =
                    harness (RenderingContext.forLanguage "en") (Some "y") Scenario.PreviewRefused

                Expect.equal
                    (run [ "case"; "close"; "--outcome"; "other"; "CC-5NRH-CGNQ-4TR4-80B2-30" ] h)
                    2
                    "Preview refusal is preserved"

                Expect.equal h.Calls.Count 3 "No execute follows a refused review"
                Expect.equal (h.ReadCount()) 0 "No confirmation follows refusal")
            testCase "nonterminal omission remains a usage error" (fun () ->
                let h = harness (RenderingContext.forLanguage "en") (Some "y") Scenario.Accepted

                unavailable
                    { h.Console with IsInteractive = false }
                    [ "case"; "close"; "--outcome"; "other"; "CC-5NRH-CGNQ-4TR4-80B2-30" ]

                match
                    Grammar.prepare
                        (fun () -> string identity)
                        [ "case"; "close"; "--outcome"; "other"; "CC-5NRH-CGNQ-4TR4-80B2-30" ]
                with
                | Error issues ->
                    Expect.contains
                        issues
                        (UsageError.MissingOption "--expected-revision")
                        "Automation must supply a revision"
                | Ok _ -> failtest "Omission cannot become an executable invocation"

                Expect.equal h.Calls.Count 0 "No remote call was made")
            testCase "JSON and CSV formats never enter interactive review" (fun () ->
                let h = harness (RenderingContext.forLanguage "en") (Some "y") Scenario.Accepted

                for format in [ "json"; "csv" ] do
                    unavailable
                        h.Console
                        [
                            "case"
                            "close"
                            "--outcome"
                            "other"
                            "CC-5NRH-CGNQ-4TR4-80B2-30"
                            "--format"
                            format
                        ]

                    unavailable
                        h.Console
                        [
                            "case"
                            "close"
                            "--outcome"
                            "other"
                            "CC-5NRH-CGNQ-4TR4-80B2-30"
                            "--format=" + format
                        ]

                Expect.isError
                    (Grammar.prepare
                        (fun () -> string identity)
                        [
                            "case"
                            "close"
                            "--outcome"
                            "other"
                            "CC-5NRH-CGNQ-4TR4-80B2-30"
                            "--format"
                            "json"
                        ])
                    "JSON omission remains usage-invalid"

                Expect.equal h.Calls.Count 0 "No remote call was made")
            testCase "omitted correction groups mean KEEP and stay out of the review" (fun () ->
                let h = harness (RenderingContext.forLanguage "en") (Some "n") Scenario.Accepted

                run [ "case"; "correct"; "CC-5NRH-CGNQ-4TR4-80B2-30" ] h |> ignore

                let values = (caseInput h.Calls[2] |> Submissions.command).Inputs.Values

                for group in [ RecordGroup.Registration; RecordGroup.Decision ] do
                    Expect.equal
                        values[TaskInputId.Target(InputTarget.Group group)]
                        (InputValue.Text "KEEP")
                        "Unselected correction groups retain their facts"

                let rendered = String.concat "\n" h.Text

                Expect.equal
                    (rendered.Split('\n')
                     |> Array.filter (fun line ->
                         line.StartsWith("Unchanged:", StringComparison.Ordinal))
                     |> Array.length)
                    0
                    "The review omits unchanged values"

                Expect.isFalse
                    (rendered.Contains "Keep current details")
                    "Unchanged group actions do not obscure the supplied changes")
            testCase "localized prompts accept affirmative Latvian and Arabic replies" (fun () ->
                for language, answer, prompt in
                    [
                        "lv", "JĀ", "Reģistrēt šīs izmaiņas? [j/N] "
                        "ar", "نعم", "هل تريد تسجيل هذه التغييرات؟ [نعم/لا] "
                    ] do
                    let h =
                        harness
                            (RenderingContext.forLanguage language)
                            (Some answer)
                            Scenario.Accepted

                    Expect.equal
                        (run
                            [
                                "case"
                                "close"
                                "--outcome"
                                "other"
                                "CC-5NRH-CGNQ-4TR4-80B2-30"
                                "--language=" + language
                            ]
                            h)
                        0
                        "Localized confirmation"

                    Expect.equal
                        (List.ofSeq h.Prompts)
                        [ prompt ]
                        "A real localized prompt is displayed"

                    Expect.equal
                        (CliInvocations.usageLanguage
                            [ "case"; "close"; "--outcome"; "other"; "--language=" + language ])
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
                let h = harness (RenderingContext.forLanguage "en") (Some "y") Scenario.Accepted

                Expect.equal
                    (run
                        [
                            "case"
                            "close"
                            "--outcome"
                            "other"
                            "CC-5NRH-CGNQ-4TR4-80B2-30"
                            "--preview"
                        ]
                        h)
                    0
                    "Preview completed"

                Expect.equal h.Calls.Count 3 "Preview does not execute"
                Expect.equal (h.ReadCount()) 0 "Preview does not request mutation consent")
            testCase "offline dry-run and explicit revisions keep their normal paths" (fun () ->
                let h = harness (RenderingContext.forLanguage "en") (Some "y") Scenario.Accepted

                unavailable
                    h.Console
                    [
                        "case"
                        "close"
                        "--outcome"
                        "other"
                        "CC-5NRH-CGNQ-4TR4-80B2-30"
                        "--dry-run"
                    ]

                unavailable
                    h.Console
                    [
                        "case"
                        "close"
                        "--outcome"
                        "other"
                        "CC-5NRH-CGNQ-4TR4-80B2-30"
                        "--expected-revision"
                        "7"
                    ]

                let draft =
                    Grammar.prepareOffline
                        [
                            "case"
                            "close"
                            "--outcome"
                            "other"
                            "CC-5NRH-CGNQ-4TR4-80B2-30"
                            "--dry-run"
                        ]
                    |> CaseBuilders.accepted
                    |> Option.get

                Expect.isNone draft.Issues "Offline syntax needs no fabricated version"

                Expect.isFalse
                    (Encoding.UTF8.GetString(draft.Bytes).Contains("expectedRevision"))
                    "No executable version is invented"

                Expect.equal h.Calls.Count 0 "No remote call was made")
            testCase "an explicit operation id is retained across the reviewed attempt" (fun () ->
                let h = harness (RenderingContext.forLanguage "en") (Some "y") Scenario.Accepted
                let supplied = Guid.Parse "80000000-0000-7000-8000-000000000099"

                run
                    [
                        "case"
                        "close"
                        "--outcome"
                        "other"
                        "CC-5NRH-CGNQ-4TR4-80B2-30"
                        "--operation-id"
                        string supplied
                    ]
                    h
                |> ignore

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

[<Tests>]
let reminderReviewDetails =
    testList
        "reminder interactive review hides technical changes"
        [
            for compound in [ false; true ] do
                testCase (if compound then "complete and plan next" else "create") (fun () ->
                    for details in [ false; true ] do
                        for consent in [ false; true ] do
                            let reminderId number =
                                ReminderId.create (
                                    Guid.Parse(sprintf "80000000-0000-7000-8000-%012d" number)
                                )
                                |> Option.get

                            let reminder number revision action state =
                                Reminder.decode
                                    (reminderId number)
                                    current.CaseId
                                    revision
                                    (ReminderAction.parse action |> CaseBuilders.accepted)
                                    (DomainInputs.date "2026-11-09")
                                    ReminderKind.FollowUp
                                    state
                                    (if state = ReminderState.Completed then
                                         Some(CalendarDate.ofDate (System.DateOnly(2026, 11, 18)))
                                     else
                                         None)
                                |> CaseBuilders.accepted

                            let before =
                                reminder
                                    1
                                    (CaseFixtures.revision 7L)
                                    "Old action"
                                    ReminderState.Pending

                            let completed =
                                reminder
                                    1
                                    (CaseFixtures.revision 8L)
                                    "Old action"
                                    ReminderState.Completed

                            let created =
                                reminder 2 Revision.first "Next action" ReminderState.Pending

                            let value reminder =
                                ReminderValue.ofPrivate
                                    (Reminder.state reminder)
                                    {
                                        Action = Reminder.action reminder
                                        Due = Reminder.due reminder
                                        Kind = Reminder.kind reminder
                                    }
                                    (Reminder.doneOn reminder)
                                |> Result.defaultWith failtest

                            let described prior reminder =
                                {
                                    DescribedPart.PartId = ReminderId.value (Reminder.id reminder)
                                    Revision = Reminder.revision reminder
                                    Description =
                                        ClaimCore.Domain.Changes.PartDescription.Reminder(
                                            ClaimCore.Domain.Changes.ReminderDescription.describe
                                                (prior |> Option.map value)
                                                (value reminder)
                                        )
                                }

                            let core =
                                {
                                    OperationReview.ChangesOwnSignInIdentity = false
                                    Task =
                                        TaskId.ofLiteral (
                                            if compound then
                                                "reminder.completeAndPlanNext"
                                            else
                                                "reminder.create"
                                        )
                                    Opening = None
                                    Target = ReviewTarget.Case(current.CaseId, current.Reference)
                                    Binding =
                                        if compound then
                                            Binding.Reminder(
                                                CaseSelector.Id current.CaseId,
                                                Reminder.id before,
                                                Reminder.revision before
                                            )
                                        else
                                            Binding.ReminderNew(CaseSelector.Id current.CaseId)
                                    References = { Actors = []; Cases = [] }
                                    Changes =
                                        (if compound then
                                             [
                                                 described (Some before) completed
                                                 described None created
                                             ]
                                         else
                                             [ described None created ])
                                    Context =
                                        {
                                            ProductVersion = "fixture"
                                            Basis = DomainInputs.workBasis
                                        }
                                }

                            let calls = ResizeArray<Invocation>()
                            use output = new System.IO.MemoryStream()
                            use errors = new System.IO.MemoryStream()
                            let mutable reviewed = ""
                            let mutable prompts = 0

                            let console: InteractiveConsole =
                                {
                                    IsInteractive = true
                                    Write = fun _ -> ()
                                    ReadLine =
                                        fun () ->
                                            prompts <- prompts + 1
                                            reviewed <- Encoding.UTF8.GetString(output.ToArray())
                                            Some(if consent then "yes" else "no")
                                }

                            let response frame (endpoint: Endpoint<'i, 'o>) value =
                                CliReply.result frame (endpoint.Output.Write value)
                                |> CaseBuilders.accepted

                            let version reminder =
                                {
                                    ReminderVersion.ReminderId = Reminder.id reminder
                                    Revision = Reminder.revision reminder
                                    Snapshot = ReminderDisclosure.Disclosed reminder
                                }

                            let invoke frame dispatched (_: CancellationToken) =
                                dispatched ()
                                calls.Add frame

                                let reply =
                                    match (Frames.endpoint frame).Token with
                                    | "context" ->
                                        response
                                            frame
                                            Api.context
                                            (ResponseExamples.defaultHuman Api.context)
                                    | "case.get" ->
                                        response
                                            frame
                                            Api.caseGet
                                            (HumanReplies.read
                                                HumanCaseDocument.ofCore
                                                (Outcome.Succeeded caseReply)
                                             |> RepresentationReply.Human)
                                    | "reminder.get" ->
                                        let item =
                                            {
                                                Reminder = before
                                                Reference = current.Reference
                                                Claimants =
                                                    [ (DomainInputs.name "Reminder claimant") ]
                                                Responsible = None
                                                RecordedAt = DomainInputs.workBasis.ObservedAt
                                                RecordedBy =
                                                    CliReplyFixtures.receipt.RecordedBy
                                                    |> AuthorityRef.actor
                                                    |> Option.get
                                                Offers =
                                                    [
                                                        Inputs.offeredTask
                                                            "reminder.completeAndPlanNext"
                                                            (Binding.Reminder(
                                                                CaseSelector.Id current.CaseId,
                                                                Reminder.id before,
                                                                Reminder.revision before
                                                            ))
                                                            Map.empty
                                                    ]
                                                Active = true
                                                Timing = None
                                            }

                                        response
                                            frame
                                            Api.reminder
                                            (HumanReplies.read
                                                ReminderScreen.document
                                                (Outcome.Succeeded
                                                    {
                                                        Basis = DomainInputs.workBasis
                                                        Item = item
                                                    })
                                             |> RepresentationReply.Human)
                                    | "operation.preview" ->
                                        let input =
                                            (Frames.value Api.operationPreview) frame |> Option.get

                                        response
                                            frame
                                            Api.operationPreview
                                            (RepresentationReply.Human(
                                                HumanReplies.previewSubmission
                                                    ((Frames.value Api.operationPreview) frame
                                                     |> Option.get)
                                                    {
                                                        OperationId = input.OperationId
                                                        Outcome =
                                                            Outcome.Succeeded(
                                                                Previewed.Reviewed core
                                                            )
                                                    }
                                            ))
                                    | "operation.execute" ->
                                        let input =
                                            (Frames.value Api.operationExecute) frame |> Option.get

                                        let receipt =
                                            { CliReplyFixtures.receipt with
                                                OperationId =
                                                    CaseFixtures.operationId (input.OperationId)
                                                Subject =
                                                    ReceiptSubject.Reminder
                                                        {
                                                            Reference = current.Reference
                                                            CaseId = current.CaseId
                                                            Act =
                                                                if compound then
                                                                    (TaskId.ofLiteral
                                                                        "reminder.completeAndPlanNext")
                                                                else
                                                                    (TaskId.ofLiteral
                                                                        "reminder.create")
                                                            Effects =
                                                                if compound then
                                                                    ReminderReceiptEffects
                                                                        .CompletedAndPlanned(
                                                                            version completed,
                                                                            version created
                                                                        )
                                                                else
                                                                    ReminderReceiptEffects.Single(
                                                                        version created
                                                                    )
                                                        }
                                            }

                                        response
                                            frame
                                            Api.operationExecute
                                            (RepresentationReply.Human(
                                                HumanReplies.execute
                                                    {
                                                        OperationId = input.OperationId
                                                        Outcome = ExecuteResult.Accepted(receipt)
                                                    }
                                            ))
                                    | other -> failtestf "Unexpected interactive endpoint %s" other

                                Task.FromResult reply

                            use processor =
                                RemoteFrameProcessor.ForTest(
                                    System.IO.Stream.Null,
                                    output,
                                    errors,
                                    invoke,
                                    (fun _ -> Task.FromResult())
                                )

                            let words =
                                [
                                    "reminder"
                                    (if compound then "complete-and-plan-next" else "create")
                                    "CC-5NRH-CGNQ-4TR4-80B2-30"
                                ]
                                @ (if compound then
                                       [ "--reminder-id"; ReminderId.format (reminderId 1) ]
                                   else
                                       [])
                                @ [
                                    "--operation-id"
                                    ReminderId.format (reminderId 2)
                                    "--action"
                                    "Next action"
                                    "--due"
                                    "2026-11-09"
                                ]
                                @ (if details then [ "--details" ] else [])

                            let code =
                                VerbRunner.run
                                    {
                                        Output = output
                                        Errors = errors
                                        Review = console
                                    }
                                    processor
                                    words
                                    CancellationToken.None
                                |> fun task -> task.GetAwaiter().GetResult()

                            Expect.equal code 0 "The real interactive verb completes"

                            Expect.equal
                                prompts
                                1
                                "The preview asks for exactly one explicit consent"

                            Expect.stringContains
                                reviewed
                                "Next action"
                                "Business reminder text survives"

                            Expect.stringContains
                                reviewed
                                "9 Nov 2026"
                                "The authored due date survives"

                            let createdId = ReminderId.format (reminderId 2)

                            Expect.equal
                                (reviewed.Contains createdId)
                                details
                                "New reminder identity is opt-in"

                            if details then
                                Expect.equal
                                    (reviewed.Split(createdId).Length - 1)
                                    2
                                    "Operation and new reminder identities are distinct technical roles even when their UUIDs coincide"

                                Expect.stringContains
                                    reviewed
                                    "Operation ID"
                                    "The command identity is labelled"

                                Expect.stringContains
                                    reviewed
                                    "Reminder ID"
                                    "The new resource identity is labelled"

                            if compound && not details then
                                Expect.isFalse
                                    (reviewed.Split('\n')
                                     |> Array.filter (fun line -> not (line.Contains "claimcore "))
                                     |> String.concat "\n"
                                     |> fun prose ->
                                         prose.Contains(ReminderId.format (reminderId 1)))
                                    "Default review does not expose the previous reminder identity"

                            let previews =
                                calls
                                |> Seq.filter (fun frame ->
                                    (Frames.endpoint frame).Id = Api.operationPreview.Id)
                                |> Seq.toList

                            let executions =
                                calls
                                |> Seq.filter (fun frame ->
                                    (Frames.endpoint frame).Id = Api.operationExecute.Id)
                                |> Seq.toList

                            Expect.equal
                                executions.Length
                                (if consent then 1 else 0)
                                "No execution precedes consent"

                            if consent then
                                Expect.equal
                                    (Frames.input executions.Head)
                                    (Frames.input previews.Head)
                                    "Execution uses the exact reviewed request"

                                if compound then
                                    match (Frames.value Api.operationExecute) executions.Head with
                                    | Some submission ->
                                        match (Submissions.command submission).Binding with
                                        | Binding.Reminder(_, _, revision) ->
                                            Expect.equal
                                                (Revision.value revision)
                                                7L
                                                "The read reminder revision is frozen"
                                        | _ -> failtest "Expected selected reminder binding"
                                    | _ ->
                                        failtest "Expected the reviewed compound reminder request")
        ]
