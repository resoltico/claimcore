module ClaimCore.Tests.CliArgumentBoundaryTests

open System
open System.IO
open Expecto
open ClaimCore.CommandLine
open ClaimCore.Cli
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire

let private required =
    function
    | Ok value -> value
    | Error errors -> failtestf "Expected admitted command: %A" errors

let private operationId = "11111111-2222-7333-8444-555555555555"

let private noAllocation () =
    failtest "Read and explicitly identified operations cannot allocate an identity"

let private words text =
    match ShellTokenizer.split text |> required with
    | "claimcore" :: arguments -> arguments
    | other -> failtestf "Expected the real executable hint: %A" other

let private actionCommand action =
    let line =
        ControlHints.action (RenderingContext.forLanguage "en") action
        |> List.exactlyOne

    let start = line.IndexOf("claimcore ", StringComparison.Ordinal)
    Expect.isGreaterThanOrEqual start 0 "The supplied action offers an executable hint"
    let command = line.Substring start

    Expect.isFalse
        (command.Contains "--operation-id")
        "Rendering does not allocate an operation identity"

    command

let private withAdmittedVerb arguments useVerb =
    let directory = Directory.CreateTempSubdirectory "claimcore-argument-boundary-"

    try
        let environment key =
            if key = "XDG_CONFIG_HOME" then
                Some directory.FullName
            else
                None

        match CliInvocations.admitConfiguredWith environment arguments |> required with
        | CliInvocation.Verb admitted -> useVerb admitted
        | other ->
            failtestf "Literal values cannot become executable help or another command: %A" other
    finally
        directory.Delete(true)

let private prepare arguments =
    withAdmittedVerb arguments (Grammar.prepare noAllocation >> required)

let private explicitOperation arguments =
    List.take 2 arguments
    @ [ "--operation-id=" + operationId ]
    @ List.skip 2 arguments

let private preview arguments =
    let prepared = prepare (explicitOperation arguments)

    Expect.isFalse
        (Frames.isProvisional prepared.Invocation)
        "An explicitly supplied operation identity is never provisional"

    let submission = Frames.value Api.operationPreview prepared.Invocation |> Option.get

    Expect.equal
        submission.OperationId
        (Guid.Parse operationId)
        "The explicit identity remains unchanged"

    Expect.isOk submission.Operation "Complete authored inputs admit through the core owner"
    submission

[<Tests>]
let tests =
    testList
        "CLI literal argument boundaries"
        [
            testCase
                "attachment withdrawal accepts either case selector and the copied hint"
                (fun () ->
                    let rawCase = "566ab687-46df-4d7e-9030-b1c31e3a122a"
                    let rawAttachment = "018f785e-1b45-7cc4-b9c9-cebf5f897689"
                    let caseId = CaseId.create (Guid.Parse rawCase) |> Option.get
                    let attachment = AttachmentId.create (Guid.Parse rawAttachment) |> Option.get
                    let byId = CaseSelector.Id caseId
                    let byReference = CaseSelector.Reference "REFERENCE"

                    let verb = [ "attachment"; "withdraw" ]

                    let machine (arguments: string list) =
                        let prepared =
                            prepare (
                                explicitOperation (
                                    verb @ arguments @ [ "--confirm"; "yes"; "--dry-run" ]
                                )
                            )

                        Frames.value Api.operationExecute prepared.Invocation |> Option.get

                    let expectBinding target (value: OperationSubmission) =
                        let input = value.Operation |> required

                        Expect.equal
                            input.Binding
                            (Binding.Attachment(target, attachment))
                            "Both tagged selectors retain the child ID"

                    machine [ "--case-id"; rawCase; rawAttachment ] |> expectBinding byId
                    machine [ "REFERENCE"; rawAttachment ] |> expectBinding byReference

                    let action =
                        TaskAffordances.offered
                            ClientCompositions.attachmentWithdrawal
                            (Some(Binding.Attachment(byId, attachment)))
                            Map.empty
                            []
                            Map.empty

                    actionCommand action |> words |> preview |> expectBinding byId

                    for extras in [ [ "REFERENCE"; rawAttachment ]; [ rawAttachment; "extra" ] ] do
                        let argv =
                            explicitOperation (
                                verb @ [ "--case-id"; rawCase ] @ extras @ [ "--preview" ]
                            )

                        Expect.isError
                            (withAdmittedVerb argv (Grammar.prepare noAllocation))
                            "Both selectors or extra operands refuse"

                    Expect.stringContains
                        (CommandGrammar.synopsis "attachment.withdraw")
                        "(--case-id UUID | [--] CASE-REFERENCE) ATTACHMENT-ID"
                        "Usage names both selectors and their common child operand")
            testCase
                "named query hints round trip option-looking text and ordered repeated filters"
                (fun () ->
                    let text = "--urgent=O'Brien"

                    let filter =
                        CaseListFilter.create
                            [ Lifecycle.Voided; Lifecycle.Active ]
                            [ CaseStatus.Closed; CaseStatus.Opened ]
                            [ CaseProgress.PartlyPaid; CaseProgress.Decided ]
                            (Some text)
                            CaseListOrder.Recent

                    let hint =
                        ControlHints.link (
                            Link.CaseList
                                {
                                    Page = { AfterCursor = None; Limit = 17 }
                                    Filter = filter
                                }
                        )

                    let request =
                        (prepare (words hint)).Invocation
                        |> Frames.value Api.caseList
                        |> Option.get

                    Expect.equal
                        request.Filter.Text
                        (Some text)
                        "A leading option prefix remains authored search text"

                    Expect.equal
                        request.Filter.Lifecycles
                        [ Lifecycle.Voided; Lifecycle.Active ]
                        "Supplied lifecycle order survives"

                    Expect.equal
                        request.Filter.Statuses
                        [ CaseStatus.Closed; CaseStatus.Opened ]
                        "Supplied status order survives"

                    Expect.equal
                        request.Filter.Progress
                        [ CaseProgress.PartlyPaid; CaseProgress.Decided ]
                        "Supplied progress order survives"

                    Expect.equal
                        request.Filter.Order
                        CaseListOrder.Recent
                        "Named selection is unchanged"

                    Expect.equal request.Page.Limit 17 "Pagination stays a named argument")
            testCase
                "query and bound operation hints preserve option-looking case references"
                (fun () ->
                    for reference in [ "--claim"; "--claim'O=Brien ; $(printf untouched)" ] do
                        let query =
                            ControlHints.link (
                                Link.CaseGet
                                    {
                                        Target = CaseSelector.Reference reference
                                    }
                            )

                        let read =
                            (prepare (words query)).Invocation
                            |> Frames.value Api.caseGet
                            |> Option.get

                        Expect.equal
                            read.Target
                            (CaseSelector.Reference reference)
                            "A copied query selects the exact reference"

                        let action =
                            TaskAffordances.offered
                                (TaskId.ofLiteral "case.close")
                                (Some(
                                    Binding.Case(
                                        CaseSelector.Reference reference,
                                        Revision.create 7L |> Option.get
                                    )
                                ))
                                (Map.ofList
                                    [
                                        TaskInputId.Target(InputTarget.Field BusinessField.Outcome),
                                        InputValue.Text "OTHER"
                                    ])
                                []
                                Map.empty

                        let submission = actionCommand action |> words |> preview

                        Expect.equal
                            (TaskId.token submission.Task)
                            "case.close"
                            "The action keeps its supplied task"

                        Expect.equal
                            submission.Envelope.Binding
                            (OperationBinding.Case(
                                CaseSelector.Reference reference,
                                7L,
                                None,
                                None
                            ))
                            "Preview retains the exact observed selector and revision")
            testCase
                "sentinel literals cannot become help preview or locale options in real admission"
                (fun () ->
                    for reference in
                        [
                            "--help"
                            "--preview"
                            "--language"
                            "--display-locale"
                            "--text=--urgent"
                            "--"
                        ] do
                        let prepared = prepare [ "case"; "get"; "--"; reference ]
                        let input = Frames.value Api.caseGet prepared.Invocation |> Option.get

                        Expect.equal
                            input.Target
                            (CaseSelector.Reference reference)
                            "Every token after the sentinel is literal"

                        Expect.equal
                            prepared.Language
                            "en"
                            "A literal language flag does not change rendering"

                    let submission =
                        preview
                            [
                                "case"
                                "close"
                                "--outcome"
                                "other"
                                "--expected-revision=7"
                                "--preview"
                                "--"
                                "--language"
                            ]

                    Expect.equal
                        (TaskId.token submission.Task)
                        "case.close"
                        "The real preview flag precedes the sentinel"

                    Expect.equal
                        submission.Envelope.Binding
                        (OperationBinding.Case(CaseSelector.Reference "--language", 7L, None, None))
                        "The later locale spelling remains the bound reference")
            testCase
                "explicit assigned named values preserve leading prefixes quotes and equals"
                (fun () ->
                    let text = "--urgent='one'=two"

                    let input =
                        (prepare [ "case"; "list"; "--text=" + text; "--limit=9" ]).Invocation
                        |> Frames.value Api.caseList
                        |> Option.get

                    Expect.equal
                        input.Filter.Text
                        (Some text)
                        "Only the first assignment separator belongs to option syntax"

                    Expect.equal input.Page.Limit 9 "Assigned numeric options still parse")
            testCase
                "operational principal hints preserve option-looking subject without a ledger identity"
                (fun () ->
                    let subject = "--subject='O'Brien'=exact"

                    let principal =
                        PrincipalKey.human "https://issuer.example/" subject |> required

                    let action =
                        {
                            Task = TaskId.ofLiteral "people.requests.dismiss"
                            LabelId = "endpoint.people.requests.dismiss.label"
                            ShortLabelId = None
                            Control =
                                ActionControl.Command(
                                    CommandRequest.DismissAccessRequest principal
                                )
                            Seeds = Map.empty
                            Editable = []
                            Choices = Map.empty
                            Blocked = None
                        }

                    let prepared = actionCommand action |> words |> prepare

                    let input =
                        Frames.value Api.peopleRequestsDismiss prepared.Invocation |> Option.get

                    Expect.isTrue prepared.DryRun "Operational hints retain dry-run"
                    Expect.equal input.Kind PrincipalKind.Human "The principal kind is unchanged"

                    Expect.equal
                        input.Issuer
                        "https://issuer.example/"
                        "The principal issuer is unchanged"

                    Expect.equal
                        input.Subject
                        subject
                        "The complete subject remains one authored value")
            testCase
                "required collection hint acquisition admits option-looking quoted case references"
                (fun () ->
                    let task = TaskId.ofLiteral "people.admit"

                    let action =
                        TaskAffordances.offered
                            task
                            (Some(Binding.PeopleNew Revision.zero))
                            Map.empty
                            (TaskAffordances.authored task)
                            Map.empty

                    let hint = actionCommand action

                    Expect.stringContains
                        hint
                        "<ROLE[@CASE-REF]>"
                        "The required collection has an acquisition row"

                    for reference in [ "--claim"; "--claim'O=Brien" ] do
                        let supplied =
                            hint
                                .Replace("'<principalKind>'", "HUMAN")
                                .Replace("'<issuer>'", "'https://issuer.example/'")
                                .Replace("'<subject>'", "new-person")
                                .Replace("'<name>'", "'Authored account'")
                                .Replace(
                                    "'<ROLE[@CASE-REF]>'",
                                    CommandGrammar.quoteArgument ("CASE_READER@" + reference)
                                )

                        let submission = supplied |> words |> preview

                        Expect.equal
                            (TaskId.token submission.Task)
                            "people.admit"
                            "Acquisition retains the atomic admission task"

                        Expect.equal
                            submission.Envelope.Binding
                            (OperationBinding.People(None, 0L))
                            "The creation binding remains exact"

                        let rows =
                            (ClaimCore.TestSupport.Submissions.command submission)
                                .Inputs.Collections[TaskCollectionId.PeopleGrants]
                            |> List.map _.Values

                        Expect.equal
                            rows
                            [
                                Map.ofList
                                    [
                                        TaskInputId.Target InputTarget.Role,
                                        InputValue.Text "CASE_READER"
                                        TaskInputId.Scope, InputValue.Text "CASE"
                                        TaskInputId.Target(
                                            InputTarget.Field BusinessField.CaseReference
                                        ),
                                        InputValue.Text reference
                                    ]
                            ]
                            "Collection rows retain exact role, scope and reference")
            testCase
                "lookup form offers required operand acquisition and empty reads invent none"
                (fun () ->
                    let hint = ControlHints.link Link.OperationLookupForm

                    Expect.stringContains
                        hint
                        "<operation-id>"
                        "A required read operand is visibly acquired"

                    let input =
                        hint.Replace("<operation-id>", operationId)
                        |> words
                        |> prepare
                        |> _.Invocation
                        |> Frames.value Api.operationGet
                        |> Option.get

                    Expect.equal
                        input.OperationId
                        (Guid.Parse operationId)
                        "The acquired read identity is exact"

                    for link, expected in
                        [ Link.Context, [ "context" ]; Link.WorkViews, [ "work"; "views" ] ] do
                        let arguments = ControlHints.link link |> words

                        Expect.equal
                            arguments
                            expected
                            "Truly empty read inputs have no invented placeholder"

                        prepare arguments |> ignore)
            testCase
                "ambiguous old named argv still refuses missing values and unknown options"
                (fun () ->
                    match
                        Grammar.prepare noAllocation [ "case"; "list"; "--text"; "--urgent" ]
                    with
                    | Ok _ -> failtest "Ambiguous named argv cannot silently become search text"
                    | Error issues ->
                        Expect.isTrue
                            (issues
                             |> List.exists (function
                                 | UsageError.MissingValue "--text" -> true
                                 | _ -> false))
                            "Missing named value remains a usage failure"

                        Expect.isTrue
                            (issues
                             |> List.exists (function
                                 | UsageError.UnknownOption("--urgent", _) -> true
                                 | _ -> false))
                            "An unknown option is not silently consumed as data")
            testCase "unsafe terminal scalars remain explicit acquisition placeholders" (fun () ->
                let hint =
                    ControlHints.link (
                        Link.CaseGet
                            {
                                Target = CaseSelector.Reference "--claim'\n\r\u001b[31m\u0007"
                            }
                    )

                Expect.isFalse
                    (hint
                     |> Seq.exists (fun c -> c = '\n' || c = '\r' || c = '\u001b' || c = '\u0007'))
                    "Copied guidance contains no terminal controls"

                Expect.equal
                    (words hint |> List.last)
                    "<value>"
                    "Unsafe data is acquired explicitly rather than re-emitted")
            testCase
                "text-file spellings after the sentinel are literals and never acquire a source"
                (fun () ->
                    let literal =
                        [ "note"; "add"; "--text=Authored note"; "--preview"; "--"; "--text-file" ]

                    let expanded = TextInputs.expand false literal |> required
                    Expect.equal expanded literal "A literal operand does not request file input"
                    let submission = preview expanded

                    Expect.equal
                        (TaskId.token submission.Task)
                        "note.add"
                        "The operation remains note entry"

                    Expect.equal
                        submission.Envelope.Binding
                        (OperationBinding.Note(CaseSelector.Reference "--text-file", None))
                        "The source-like operand remains the exact case reference"

                    let directory = Directory.CreateTempSubdirectory "claimcore-no-text-io-"

                    try
                        let missing = Path.Combine(directory.FullName, "must-not-open.txt")
                        let poison = [ "note"; "add"; "--"; "--text-file"; missing ]

                        Expect.equal
                            (TextInputs.expand false poison |> required)
                            poison
                            "Even an existing source spelling followed by a missing file cannot trigger I/O after the sentinel"

                        Expect.isFalse (File.Exists missing) "No source file was created"
                    finally
                        directory.Delete(true))
            testCase
                "file text beginning with options expands to one assigned value and prepares exact inputs"
                (fun () ->
                    let directory = Directory.CreateTempSubdirectory "claimcore-option-text-"

                    try
                        let path = Path.Combine(directory.FullName, "note.txt")
                        let source = "--help='O'Brien'=one\r\n--language"
                        File.WriteAllText(path, source, System.Text.UTF8Encoding(false))

                        let expanded =
                            TextInputs.expand
                                false
                                [
                                    "note"
                                    "add"
                                    "--text-file"
                                    path
                                    "--preview"
                                    "--"
                                    "--claim"
                                ]
                            |> required

                        Expect.isTrue
                            (expanded
                             |> List.exists (fun value ->
                                 value.StartsWith("--text=", StringComparison.Ordinal)))
                            "Source acquisition emits an unambiguous assigned value"

                        let quoted =
                            expanded |> List.map CommandGrammar.quoteArgument |> String.concat " "

                        let submission = ShellTokenizer.split quoted |> required |> preview

                        Expect.equal
                            (TaskId.token submission.Task)
                            "note.add"
                            "The source does not become a help invocation"

                        Expect.equal
                            submission.Envelope.Binding
                            (OperationBinding.Note(CaseSelector.Reference "--claim", None))
                            "Source expansion preserves the option-looking selector"

                        let command = ClaimCore.TestSupport.Submissions.command submission

                        Expect.equal
                            command.Inputs.Values[TaskInputId.Target InputTarget.NoteText]
                            (InputValue.Text "--help='O'Brien'=one\n--language")
                            "Only declared newline normalization changes the acquired text"

                        Expect.equal
                            (File.ReadAllText path)
                            source
                            "Acquisition never rewrites the authored file"
                    finally
                        directory.Delete(true))
            testCase
                "a literal dry-run operand cannot select the offline path or alter delivery metadata"
                (fun () ->
                    let arguments =
                        explicitOperation
                            [
                                "case"
                                "close"
                                "--outcome"
                                "other"
                                "--expected-revision=7"
                                "--"
                                "--dry-run"
                            ]

                    Expect.isNone
                        (Grammar.prepareOffline arguments |> required)
                        "Only options before the sentinel can request offline drafting"

                    let prepared = prepare arguments
                    Expect.isFalse prepared.DryRun "The literal does not request local dry-run"

                    let submission =
                        Frames.value Api.operationExecute prepared.Invocation |> Option.get

                    Expect.equal
                        (TaskId.token submission.Task)
                        "case.close"
                        "The selected operation remains unchanged"

                    Expect.equal
                        submission.Envelope.Binding
                        (OperationBinding.Case(CaseSelector.Reference "--dry-run", 7L, None, None))
                        "Delivery metadata does not consume the operand")

        ]
