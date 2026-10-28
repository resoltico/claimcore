module ClaimCore.Tests.InteractiveReviewTests

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Protocol
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
        Snapshot = current
        Advice = CaseAdvice.create [] None [] |> Fixtures.accepted
    }

let private review =
    OperationReview.Case
        {
            Before = Some current
            Proposed =
                { current with
                    Revision = CaseFixtures.revision 8L
                }
            Changes = []
            Context =
                {
                    ProductVersion = "synthetic"
                    EffectiveBusinessDate = DateOnly(2026, 9, 7)
                    TimeZoneId = "Etc/UTC"
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
            | "case.get" ->
                Expect.equal
                    generated
                    0
                    "The automatically allocated id is not frozen before reading the case"

                response
                    frame
                    Api.caseGet.Output
                    (if scenario = Scenario.ReadRefused then
                         Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable)
                     else
                         Outcome.Succeeded caseReply)
            | "operation.preview" ->
                response
                    frame
                    Api.operationPreview.Output
                    {
                        OperationId = operationId ()
                        Outcome =
                            if scenario = Scenario.PreviewRefused then
                                Outcome.Rejected(Refusal.Access AccessRefusal.ActNotPermitted)
                            else
                                Outcome.Succeeded(Previewed.Reviewed review)
                    }
            | "operation.execute" ->
                let receipt =
                    { CliReplyFixtures.receipt with
                        OperationId = CaseFixtures.operationId (operationId ())
                    }

                response
                    frame
                    Api.operationExecute.Output
                    {
                        OperationId = operationId ()
                        Outcome =
                            if scenario = Scenario.Stale then
                                ExecuteResult.Rejected(
                                    Refusal.RevisionConflict(CaseFixtures.revision 8L)
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
                "accept freezes one id and fetched revision across preview and execute"
                (fun () ->
                    let h = harness "en" (Some "yes") Scenario.Accepted
                    Expect.equal (run [ "case"; "close"; "x" ] h) 0 "Accepted execution"

                    Expect.equal
                        (endpoints h)
                        [ "case.get"; "operation.preview"; "operation.execute" ]
                        "Read, review, then execute"

                    Expect.equal (h.Generated()) 1 "Allocate one fresh operation id after the read"
                    Expect.equal (h.ReadCount()) 1 "Consent is requested once"
                    let pending = caseInput h.Calls[1]

                    Expect.equal
                        pending.ExpectedRevision
                        7L
                        "The server read supplies the expected revision"

                    Expect.equal
                        pending.OperationId
                        identity
                        "The fresh id belongs to the reviewed operation"

                    Expect.equal
                        (Frames.inputBytes h.Calls[1])
                        (Frames.inputBytes h.Calls[2])
                        "Confirmation executes exactly the reviewed input")
            testCase "decline never dispatches execute" (fun () ->
                let h = harness "en" (Some "n") Scenario.Accepted

                Expect.equal
                    (run [ "case"; "close"; "x" ] h)
                    0
                    "Decline is a completed human choice"

                Expect.equal
                    (endpoints h)
                    [ "case.get"; "operation.preview" ]
                    "No mutation follows decline")
            testCase "empty or closed confirmation input defaults to no" (fun () ->
                for answer in [ None; Some ""; Some "unexpected" ] do
                    let h = harness "en" answer Scenario.Accepted
                    run [ "case"; "close"; "x" ] h |> ignore
                    Expect.equal h.Calls.Count 2 "Only an explicit affirmative answer can execute")
            testCase "a stale revision stays a definite refusal without retry" (fun () ->
                let h = harness "en" (Some "y") Scenario.Stale

                Expect.equal
                    (run [ "case"; "close"; "x" ] h)
                    2
                    "The service still enforces its locked revision"

                Expect.equal
                    h.Calls.Count
                    3
                    "The client does not fetch a new revision and bypass consent"

                Expect.equal
                    (caseInput h.Calls[2]).ExpectedRevision
                    7L
                    "Execute retains the reviewed revision")
            testCase "a refused read stops before id allocation preview or confirmation" (fun () ->
                let h = harness "en" (Some "y") Scenario.ReadRefused
                Expect.equal (run [ "case"; "close"; "x" ] h) 2 "Read refusal is preserved"
                Expect.equal (endpoints h) [ "case.get" ] "No further operation is attempted"
                Expect.equal (h.Generated()) 0 "No reviewed operation was created"
                Expect.equal (h.ReadCount()) 0 "A refusal cannot invite consent")
            testCase "a refused preview stops before confirmation or execute" (fun () ->
                let h = harness "en" (Some "y") Scenario.PreviewRefused
                Expect.equal (run [ "case"; "close"; "x" ] h) 2 "Preview refusal is preserved"
                Expect.equal h.Calls.Count 2 "No execute follows a refused review"
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
            testCase "omitted correction groups mean KEEP and every action is shown" (fun () ->
                let h = harness "en" (Some "n") Scenario.Accepted
                run [ "case"; "correct"; "x" ] h |> ignore

                match (caseInput h.Calls[1]).Act with
                | CaseAct.Correct correction ->
                    Expect.equal correction.Registration Amendment.Keep "Registration is retained"
                    Expect.equal correction.Decision Change.Keep "Decision is retained"
                    Expect.equal correction.Payment Change.Keep "Payment is retained"
                | _ -> failtest "Expected a correction review"

                let rendered = String.concat "\n" h.Text

                for group in [ "Registration"; "Payment decision"; "Payment record" ] do
                    Expect.stringContains
                        rendered
                        (group + ": Keep current details")
                        "The review makes unchanged groups explicit")
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
            testCase "explicit preview remains preview-only without asking to execute" (fun () ->
                let h = harness "en" (Some "y") Scenario.Accepted
                Expect.equal (run [ "case"; "close"; "x"; "--preview" ] h) 0 "Preview completed"
                Expect.equal h.Calls.Count 2 "Preview does not execute"
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
                    (caseInput h.Calls[1]).OperationId
                    supplied
                    "Preview retains the explicit id"

                Expect.equal
                    (Frames.inputBytes h.Calls[1])
                    (Frames.inputBytes h.Calls[2])
                    "Execute retains the same explicit id")
        ]
