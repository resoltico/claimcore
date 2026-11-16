module ClaimCore.Tests.PeopleInteractiveReviewTests

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

let private operation = Guid.Parse "6f91c638-a33f-793d-9a32-000000000001"

let private actor = Guid.Parse "10000000-0000-7000-8000-000000000001"
let private baseWords verb = [ "people"; verb; actor.ToString("D") ]

let private command (submission: OperationSubmission) = submission

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
                            response frame Api.context (ResponseExamples.defaultHuman Api.context)
                        elif (Frames.endpoint frame).Id = Api.peopleGrants.Id then
                            let person =
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
                                Api.peopleGrants
                                (RepresentationReply.Human(
                                    HumanReplies.read
                                        HumanReadDocuments.grants
                                        (Outcome.Succeeded
                                            {
                                                Revision = Revision.first
                                                Controls =
                                                    [
                                                        "people.setProfile"
                                                        "people.disable"
                                                        "people.eraseProfile"
                                                    ]
                                                    |> List.map (fun task ->
                                                        let seeds =
                                                            if task = "people.setProfile" then
                                                                Map.ofList
                                                                    [
                                                                        TaskInputId.Target
                                                                            InputTarget
                                                                                .ProfileDisplayName,
                                                                        InputValue.Text
                                                                            "Current person"
                                                                    ]
                                                            else
                                                                Map.empty

                                                        Inputs.offeredTask
                                                            task
                                                            (Binding.People(
                                                                ActorId.create actor |> Option.get,
                                                                Revision.first
                                                            ))
                                                            seeds)
                                                Basis = Inputs.workBasis
                                                Actor = person
                                                Offers =
                                                    [
                                                        (TaskId.ofLiteral "people.setProfile")
                                                        (TaskId.ofLiteral "people.disable")
                                                        (TaskId.ofLiteral "people.eraseProfile")
                                                    ]
                                                ProfileSeed =
                                                    Some
                                                        {
                                                            DisplayName = "Current person"
                                                            Contact = None
                                                        }
                                                Items = []
                                                NextCursor = None
                                            })
                                ))
                        elif (Frames.endpoint frame).Id = Api.operationPreview.Id then
                            let request =
                                (Frames.value Api.operationPreview >> Option.map command) frame
                                |> Option.get

                            let people = request

                            let afterProfile, enabled =
                                match TaskId.token people.Task with
                                | "people.setProfile" ->
                                    let text target =
                                        people.Envelope.Inputs[TaskInputId.Target target]
                                        |> function
                                            | SubmissionValue.Text text -> text
                                            | _ -> failtest "Text expected"

                                    Some(
                                        ActorProfile.parse
                                            {
                                                DisplayName = text InputTarget.ProfileDisplayName
                                                Contact = None
                                            }
                                        |> Fixtures.accepted
                                    ),
                                    true
                                | "people.disable" -> Some profile, false
                                | "people.eraseProfile" -> None, true
                                | _ -> failtest "Unexpected person task"

                            let change before after =
                                if before = after then
                                    None
                                else
                                    Some
                                        {
                                            ValueChange.Before = before
                                            After = after
                                        }

                            let description =
                                {
                                    PersonPartDescription.Profiles =
                                        {
                                            Before = Some profile
                                            After = afterProfile
                                        }
                                    Principal = None
                                    Enabled = change (Some true) (Some enabled)
                                    ProfilePresent = change (Some true) (Some afterProfile.IsSome)
                                    DisplayName =
                                        change
                                            (Some profile.DisplayName)
                                            (afterProfile |> Option.map _.DisplayName)
                                    Contact = None
                                    Grants = []
                                }

                            let review =
                                {
                                    OperationReview.Task = people.Task
                                    Binding = (Submissions.command people).Binding
                                    Target = ReviewTarget.Person(Some targetActor)
                                    References = { Actors = [ targetActor ]; Cases = [] }
                                    Context =
                                        {
                                            ProductVersion = "fixture"
                                            Basis = Inputs.workBasis
                                        }
                                    Changes =
                                        [
                                            {
                                                PartId = actor
                                                Revision = Revision.create 2L |> Option.get
                                                Description = PartDescription.Person description
                                            }
                                        ]
                                }

                            response
                                frame
                                Api.operationPreview
                                (RepresentationReply.Human(
                                    HumanReplies.previewSubmission
                                        (Frames.value Api.operationPreview frame |> Option.get)
                                        {
                                            PreviewReply.OperationId = people.OperationId
                                            Outcome = Outcome.Succeeded(Previewed.Reviewed(review))
                                        }
                                ))
                        elif (Frames.endpoint frame).Id = Api.operationExecute.Id then
                            let request =
                                (Frames.value Api.operationExecute >> Option.map command) frame
                                |> Option.get

                            let people = request
                            let kind = people.Task

                            let receipt =
                                { CliReplyFixtures.receipt with
                                    OperationId = CaseFixtures.operationId people.OperationId
                                    Subject =
                                        ReceiptSubject.People
                                            {
                                                Act = kind
                                                TargetActor =
                                                    PeopleTargetDisclosure.Disclosed targetActor
                                                Effects =
                                                    [
                                                        {
                                                            Act = kind
                                                            Id =
                                                                EffectId.create
                                                                    (CaseFixtures.operationId
                                                                        people.OperationId)
                                                                    0
                                                                |> Option.get
                                                            Position =
                                                                EffectPosition.create
                                                                    CliReplyFixtures.receipt
                                                                        .Position.Sequence
                                                                    0
                                                                |> Option.get
                                                        }
                                                    ]
                                            }
                                }

                            response
                                frame
                                Api.operationExecute
                                (RepresentationReply.Human(
                                    HumanReplies.execute
                                        {
                                            ExecuteReply.OperationId = people.OperationId
                                            Outcome = ExecuteResult.Accepted(receipt)
                                        }
                                ))
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
        "interactive people and offline intent"
        [
            testCase "explicit revision flags bypass interactive acquisition" (fun () ->
                let console =
                    {
                        IsInteractive = true
                        ReadLine = (fun () -> None)
                        Write = ignore
                    }

                for words in
                    [
                        baseWords "disable" @ [ "--expected-revision"; "2" ]
                        [ "case"; "erase"; "CASE-1"; "--expected-family-sequence"; "10" ]
                    ] do
                    Expect.isNone
                        (InteractiveReview.prepare console words |> Fixtures.accepted)
                        "Explicit preconditions use the ordinary automation grammar"

                let prepared =
                    Grammar.prepare
                        (fun () -> operation.ToString("D"))
                        (baseWords "disable" @ [ "--expected-revision"; "2" ])
                    |> Fixtures.accepted

                let input = Frames.value Api.operationExecute prepared.Invocation |> Option.get

                Expect.equal
                    input.Envelope.Binding
                    (OperationBinding.People(Some actor, 2L))
                    "An explicit revision remains unchanged")
            testCase "ordinary people previews then executes identical ID and bytes" (fun () ->
                let code, calls, text, ids =
                    run (baseWords "set-profile" @ [ "--name"; "Changed person" ]) (Some "yes")

                Expect.equal code 0 "Accepted"

                Expect.equal
                    (calls |> List.map (Frames.endpoint >> _.Token))
                    [ "context"; "people.grants"; "operation.preview"; "operation.execute" ]
                    "The person read supplies the ready binding before preview"

                Expect.equal ids 1 "Allocate only one ID after valid syntax"

                Expect.equal
                    (calls
                     |> List.find (fun frame ->
                         (Frames.endpoint frame).Token = "operation.preview")
                     |> Frames.inputBytes)
                    (calls
                     |> List.find (fun frame ->
                         (Frames.endpoint frame).Token = "operation.execute")
                     |> Frames.inputBytes)
                    "The exact reviewed request is executed"

                Expect.stringContains text "Current person" "The target has its core current name")
            testCase "stewardship statement is shown and decline never writes" (fun () ->
                let code, calls, text, _ = run (baseWords "disable") (Some "no")
                Expect.equal code 0 "Decline completes normally"

                Expect.equal
                    (calls |> List.map (Frames.endpoint >> _.Token))
                    [ "context"; "people.grants"; "operation.preview" ]
                    "No mutation"

                let key = PresentationKeys.consequence ((TaskId.ofLiteral "people.disable"))

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
            testCase "malformed case work reminder and people drafts make zero exchanges" (fun () ->
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

                    Expect.isEmpty calls "No context target or preview read occurs before syntax"

                    Expect.equal ids 0 "Invalid intent never allocates an identity"

                    Expect.isFalse (String.IsNullOrWhiteSpace text) "All local issues are rendered")
            testCase
                "supplied invalid intent metadata still fails without fabricating missing metadata"
                (fun () ->
                    let valid: SubmissionEnvelope<int64 option, Guid option> =
                        {
                            OperationId = None
                            Task = TaskId.ofLiteral "case.close"
                            Binding =
                                OperationBinding.Case(
                                    CaseSelector.Reference "R-1",
                                    None,
                                    None,
                                    None
                                )
                            Inputs = Map.empty
                        }

                    Expect.equal
                        (OperationSubmission.syntaxDraft valid)
                        (Ok None)
                        "Absent identity and revision remain absent"

                    let invalid =
                        { valid with
                            OperationId = Some Guid.Empty
                            Binding =
                                OperationBinding.Case(
                                    CaseSelector.Reference "R-1",
                                    Some -1L,
                                    None,
                                    None
                                )
                        }

                    let issues =
                        OperationSubmission.syntaxDraft invalid
                        |> Fixtures.accepted
                        |> Option.get
                        |> InputIssues.toList

                    Expect.equal
                        (issues |> List.map _.Target)
                        [ InputTarget.OperationId; InputTarget.ExpectedRevision ]
                        "Supplied metadata checks accumulate in canonical order")
        ]
