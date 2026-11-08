module ClaimCore.Tests.AuthorityInteractiveReviewTests

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

let private operation = Guid.Parse "6f91c638-a33f-793d-9a32-000000000001"

let private baseWords verb =
    [
        "authority"
        verb
        "--principal-kind"
        "human"
        "--issuer"
        "https://intent.example/"
        "--subject"
        "person"
    ]

let private targetActor =
    { CliReplyFixtures.receipt.RecordedBy with
        DisplayName = DisplayName.Named(Inputs.name "Current person")
    }

let private profile =
    ActorProfile.parse
        {
            DisplayName = "Current person"
            Contact = None
        }
    |> Fixtures.accepted

let private run words (answer: string option) =
    let calls = ResizeArray<Invocation>()
    let output = StringBuilder()
    let mutable ids = 0

    let console: InteractiveConsole =
        {
            IsInteractive = true
            ReadLine = fun () -> answer
            Write = fun bytes -> output.Append(Encoding.UTF8.GetString(bytes: byte array)) |> ignore
        }

    let response frame (endpoint: Endpoint<'input, 'output>) (value: 'output) =
        CliReply.result frame (endpoint.Output.Write value) |> Fixtures.accepted

    let transport: InteractiveTransport =
        {
            Exchange =
                fun frame _ ->
                    calls.Add frame

                    let reply =
                        if (Frames.endpoint frame).Id = Api.context.Id then
                            response frame Api.context (ResponseExamples.defaultReply Api.context)
                        elif (Frames.endpoint frame).Id = Api.operationPreview.Id then
                            let request = Frames.value Api.operationPreview frame |> Option.get

                            let authority =
                                match request with
                                | OperationInput.Authority value -> value
                                | _ -> failtest "Expected authority input"

                            let before =
                                {
                                    AuthorityState.Profile = Some profile
                                    Enabled = true
                                    Grants = []
                                    Access = []
                                }

                            let after, effect =
                                match authority.Act with
                                | AuthorityAct.SetProfile input ->
                                    let value = ActorProfile.parse input |> Fixtures.accepted in

                                    { before with Profile = Some value },
                                    AuthorityReviewEffect.SetProfile value
                                | AuthorityAct.Disable ->
                                    { before with Enabled = false }, AuthorityReviewEffect.Disable
                                | AuthorityAct.EraseProfile ->
                                    { before with Profile = None },
                                    AuthorityReviewEffect.EraseProfile
                                | _ -> failtest "Unsupported review scenario"

                            let target =
                                {
                                    ActorReviewItem.Actor = targetActor
                                    Principal =
                                        PrincipalKey.human "https://intent.example/" "person"
                                        |> Fixtures.accepted
                                    Contact = None
                                    State = AccountState.Active
                                    Roles = []
                                    ResponsibleCases = None
                                }

                            response
                                frame
                                Api.operationPreview
                                (PresentedPreviewReply.ofCore
                                    (Some(OperationInput.Authority authority))
                                    {
                                        PreviewReply.OperationId = authority.OperationId
                                        Outcome =
                                            Outcome.Succeeded(
                                                Previewed.Reviewed(
                                                    OperationReview.Authority
                                                        {
                                                            Basis = Inputs.workBasis
                                                            Target = Some target
                                                            AccessScope = GrantScope.Installation
                                                            Before = before
                                                            After = after
                                                            Effects = [ effect ]
                                                        }
                                                )
                                            )
                                    })
                        elif (Frames.endpoint frame).Id = Api.operationExecute.Id then
                            let request = Frames.value Api.operationExecute frame |> Option.get

                            let authority =
                                match request with
                                | OperationInput.Authority value -> value
                                | _ -> failtest "Expected authority input"

                            let kind = AuthorityAct.kind authority.Act

                            let receipt =
                                { CliReplyFixtures.receipt with
                                    OperationId = CaseFixtures.operationId authority.OperationId
                                    Subject =
                                        ReceiptSubject.Authority
                                            {
                                                Act = kind
                                                TargetActor =
                                                    AuthorityTargetDisclosure.Disclosed targetActor
                                                Effects =
                                                    [
                                                        {
                                                            Act = kind
                                                            Position =
                                                                CliReplyFixtures.receipt.Position
                                                        }
                                                    ]
                                            }
                                }

                            response
                                frame
                                Api.operationExecute
                                {
                                    PresentedExecuteReply.OperationId = authority.OperationId
                                    Outcome =
                                        PresentedExecuteResult.Accepted(
                                            Presented.create
                                                (Descriptor.receipt >> Descriptor.recordView)
                                                receipt
                                        )
                                }
                        else
                            failtest "Unexpected exchange"

                    Task.FromResult reply
            Deliver =
                fun frame reply ->
                    output.Append(
                        ReplyText.bytesFor "en" (Some frame) reply.Frame |> Encoding.UTF8.GetString
                    )
                    |> ignore
        }

    let draft =
        InteractiveReview.prepare console words
        |> Fixtures.accepted
        |> Option.defaultWith (fun () -> failtest "Expected interactive draft")

    let code =
        InteractiveReview.run
            (fun () ->
                ids <- ids + 1
                operation)
            console
            transport
            draft
            CancellationToken.None
        |> _.GetAwaiter().GetResult()

    code, List.ofSeq calls, output.ToString(), ids

[<Tests>]
let tests =
    testList
        "interactive authority and offline intent"
        [
            testCase "ordinary authority previews then executes identical ID and bytes" (fun () ->
                let code, calls, text, ids =
                    run (baseWords "set-profile" @ [ "--name"; "Changed person" ]) (Some "yes")

                Expect.equal code 0 "Accepted"

                Expect.equal
                    (calls |> List.map (Frames.endpoint >> _.Token))
                    [ "context"; "operation.preview"; "operation.execute" ]
                    "No case read is invented for authority"

                Expect.equal ids 1 "Allocate only one ID after valid syntax"

                Expect.equal
                    (Frames.inputBytes calls[1])
                    (Frames.inputBytes calls[2])
                    "The exact reviewed request is executed"

                Expect.stringContains text "Current person" "The target has its core current name")
            testCase "stewardship statement is shown and decline never writes" (fun () ->
                let code, calls, text, _ = run (baseWords "disable") (Some "no")
                Expect.equal code 0 "Decline completes normally"

                Expect.equal
                    (calls |> List.map (Frames.endpoint >> _.Token))
                    [ "context"; "operation.preview" ]
                    "No mutation"

                let key =
                    OperationCatalog.consequenceId (
                        CatalogOperation.Act(ActKind.Authority AuthorityKind.Disable)
                    )
                    |> Option.get

                Expect.stringContains
                    text
                    (MessageCatalogs.render "en" key Map.empty)
                    "Core consequence statement is retained"

                Expect.stringContains text "Not recorded." "Clear decline output")
            testCase "profile erasure requires exact operation target rather than yes" (fun () ->
                for answer, expectedWrites in
                    [
                        Some "yes", 0
                        None, 0
                        Some(operation.ToString("D") + " "), 0
                        Some(operation.ToString("D")), 1
                    ] do
                    let code, calls, text, _ = run (baseWords "erase-profile") answer
                    Expect.equal code 0 "Human completion"

                    Expect.equal
                        (calls
                         |> List.filter (fun frame ->
                             (Frames.endpoint frame).Id = Api.operationExecute.Id)
                         |> List.length)
                        expectedWrites
                        "Only exact typed ID permits execution"

                    Expect.stringContains
                        text
                        ("Type " + operation.ToString("D") + " to confirm:")
                        "Exact target prompt")
            testCase
                "malformed case work reminder and authority drafts make zero exchanges"
                (fun () ->
                    let invalid =
                        [
                            [ "case"; "amend-registration"; "R-1"; "--claimant-name"; "" ]
                            [ "work"; "update"; "R-1"; "--handling-summary"; "" ]
                            [ "reminder"; "create"; "R-1"; "--action"; ""; "--due"; "2026-11-04" ]
                            baseWords "set-profile" @ [ "--name"; "" ]
                        ]

                    for words in invalid do
                        let code, calls, text, ids = run words None
                        Expect.equal code 2 "Core syntax refusal is local"

                        Expect.isEmpty
                            calls
                            "No context target or preview read occurs before syntax"

                        Expect.equal ids 0 "Invalid intent never allocates an identity"

                        Expect.isFalse
                            (String.IsNullOrWhiteSpace text)
                            "All local issues are rendered")
            testCase
                "supplied invalid intent metadata still fails without fabricating missing metadata"
                (fun () ->
                    let valid =
                        AuthoredIntent.Case(None, Some "R-1", None, CaseAct.Bare BareAct.Close)

                    Expect.equal
                        (Validation.syntaxIntent valid)
                        None
                        "Omitted identity and revision are legitimately absent"

                    let invalid =
                        AuthoredIntent.Case(
                            Some Guid.Empty,
                            Some "R-1",
                            Some -1L,
                            CaseAct.Bare BareAct.Close
                        )

                    let issues =
                        Validation.syntaxIntent invalid |> Option.get |> InputIssues.toList

                    Expect.equal
                        (issues |> List.map _.Target)
                        [ InputTarget.OperationId; InputTarget.ExpectedRevision ]
                        "Supplied metadata checks accumulate in canonical order")
        ]
