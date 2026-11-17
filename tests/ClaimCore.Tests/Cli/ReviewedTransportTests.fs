module ClaimCore.Tests.ReviewedTransportTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Cli
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.TestSupport

let private required result =
    result |> Result.defaultWith (fun error -> failtestf "%A" error)

let private sync (pending: Task<'value>) = pending.GetAwaiter().GetResult()
let private installation = Guid.Parse "90000000-0000-4000-8000-000000000001"
let private otherInstallation = Guid.Parse "90000000-0000-4000-8000-000000000002"

type private Clock() =
    inherit TimeProvider()
    let mutable now = DateTimeOffset.UtcNow
    override _.GetUtcNow() = now
    override _.GetTimestamp() = now.UtcTicks
    override _.TimestampFrequency = TimeSpan.TicksPerSecond
    member _.Advance duration = now <- now.Add duration

let private token (clock: Clock) =
    AccessTokens.fromPartsAt
        {
            Clock = clock
            Utc = clock.GetUtcNow()
            Timestamp = clock.GetTimestamp()
        }
        "Bearer"
        "synthetic-token"
        3600
    |> required

let private configuration =
    RemoteConfiguration.create
        "https://service.example/"
        "https://issuer.example/"
        "synthetic-client"
        RemoteClientMode.Interactive
        None
    |> required

let private contextReply representation installation =
    let data =
        match ResponseExamples.forRepresentation Representation.Data Api.context with
        | RepresentationReply.Data(Outcome.Succeeded data) ->
            match data.Requester with
            | RequesterState.Active actor ->
                { data with
                    Requester =
                        RequesterState.Active
                            { actor with
                                InstallationId = installation
                            }
                }
            | _ -> failtest "Active fixture required"
        | _ -> failtest "Data fixture required"

    match representation with
    | Representation.Data -> RepresentationReply.Data(Outcome.Succeeded data)
    | Representation.Human ->
        RepresentationReply.Human(
            HumanReplies.read HumanReadDocuments.context (Outcome.Succeeded data)
        )
    |> Codec.encode Api.context.Output

let private operation (request: string) =
    request
    |> Encoding.UTF8.GetBytes
    |> StrictJson.read Limits.MaximumRequestBytes
    |> required
    |> JsonValue.tryMember "operationId"
    |> Option.bind (function
        | JsonValue.String value -> Some(Guid.Parse value)
        | _ -> None)
    |> Option.get

let private previewReply id irreversible =
    let reviewed =
        ResponseExamples.samples Api.operationPreview.Token
        |> List.pick (fun bytes ->
            match Codec.decode Limits.MaximumResponseBytes Api.operationPreview.Output bytes with
            | Ok(RepresentationReply.Human {
                                               Outcome = HumanPreviewResult.Reviewed review
                                           }) -> Some review
            | _ -> None)

    let reviewed =
        if irreversible then
            { reviewed with
                Document =
                    { reviewed.Document with
                        Subject =
                            DocumentSubject.Review(
                                ConfirmationTarget.CaseReference(DomainInputs.reference "CASE-1"),
                                Consequence.Irreversible
                            )
                    }
            }
        else
            reviewed

    RepresentationReply.Human
        {
            OperationId = id
            Outcome = HumanPreviewResult.Reviewed reviewed
        }
    |> Codec.encode Api.operationPreview.Output

let private acceptedReply id =
    let receipt =
        ResponseExamples.samples Api.operationExecute.Token
        |> List.pick (fun bytes ->
            match Codec.decode Limits.MaximumResponseBytes Api.operationExecute.Output bytes with
            | Ok(RepresentationReply.Human {
                                               Outcome = HumanExecuteResult.Accepted document
                                           }) -> Some document
            | _ -> None)

    let document =
        { receipt with
            Subject = DocumentSubject.Receipt(OperationId.create id |> Option.get)
            Links = []
        }

    RepresentationReply.Human
        {
            OperationId = id
            Outcome = HumanExecuteResult.Accepted document
        }
    |> Codec.encode Api.operationExecute.Output

let private reply bytes = CliTransportFixture.response 200 bytes

let private representation (request: HttpRequestMessage) =
    if
        request.Headers.Contains Representation.HeaderName
        && (request.Headers.GetValues Representation.HeaderName |> Seq.head) = "HUMAN"
    then
        Representation.Human
    else
        Representation.Data

[<Tests>]
let tests =
    testList
        "reviewed native transport boundaries"
        [
            testCase
                "operational commands send once through the actual processor without ledger recovery"
                (fun () ->
                    for failDelivery in [ false; true ] do
                        let clock = Clock()
                        let mutable calls = 0
                        let mutable journals = 0

                        use handler =
                            new CliTransportFixture.Handler(fun request ->
                                calls <- calls + 1

                                Expect.equal
                                    (request.RequestUri
                                     |> Option.ofObj
                                     |> Option.get
                                     |> _.AbsolutePath)
                                    "/api/people.requests.dismiss"
                                    "Correct bound endpoint"

                                if failDelivery then
                                    Task.FromException<HttpResponseMessage>(
                                        HttpRequestException "Synthetic acknowledgement loss"
                                    )
                                else
                                    Task.FromResult(
                                        reply (
                                            Codec.encode
                                                Api.peopleRequestsDismiss.Output
                                                (RepresentationReply.Data(Outcome.Succeeded()))
                                        )
                                    ))

                        use client = new HttpClient(handler)
                        use output = new MemoryStream()
                        use errors = new MemoryStream()

                        let access =
                            {
                                Configuration = configuration
                                Client = client
                                IsCurrent = (fun _ -> true)
                                Clock = clock
                                JournalDirectory =
                                    fun () ->
                                        journals <- journals + 1
                                        Error()
                            }

                        use processor =
                            RemoteFrameProcessor.ForAuthenticatedTest(
                                {
                                    Input = Stream.Null
                                    Output = output
                                    Errors = errors
                                },
                                access,
                                token clock
                            )

                        let frame =
                            Frames.create
                                Api.peopleRequestsDismiss
                                {
                                    PrincipalInput.Kind = PrincipalKind.Human
                                    Issuer = "https://issuer.example/"
                                    Subject = "synthetic-person"
                                }
                            |> required

                        let response = processor.Exchange(frame, CancellationToken.None) |> sync
                        processor.Deliver((fun reply -> reply.Bytes), response)
                        Expect.equal calls 1 "No retry or context/journal detour"
                        Expect.equal journals 0 "Ephemeral command never opens ledger recovery"

                        Expect.equal
                            response.ExitCode
                            (if failDelivery then 4 else 0)
                            "Uncertain operational delivery stays uncertain"

                        Expect.equal response.Frame.OperationId None "No invented ledger identity"

                        Expect.isFalse
                            ((Encoding.UTF8.GetString(errors.ToArray())).Contains "operation.get")
                            "No ledger lookup/retry guidance")
            testCase
                "a long actual interactive review preserves preview identity and bytes"
                (fun () ->
                    let clock = Clock()

                    let directory =
                        Directory.CreateTempSubdirectory("claimcore-reviewed-transport-")

                    try
                        let bodies = ResizeArray<string * string>()

                        use handler =
                            new CliTransportFixture.Handler(fun request ->
                                task {
                                    let path =
                                        (request.RequestUri |> Option.ofObj |> Option.get)
                                            .AbsolutePath

                                    match path with
                                    | "/api/context" ->
                                        return
                                            reply (
                                                contextReply (representation request) installation
                                            )
                                    | "/api/operation.preview" ->
                                        Expect.equal
                                            (request.Headers.GetValues("ClaimCore-Installation")
                                             |> Seq.exactlyOne)
                                            (installation.ToString("D"))
                                            "Preview carries its observed installation"

                                        let! body =
                                            (request.Content
                                             |> Option.ofObj
                                             |> Option.defaultWith (fun () ->
                                                 failtest "Expected request body"))
                                                .ReadAsStringAsync()

                                        bodies.Add(path, body)
                                        return reply (previewReply (operation body) false)
                                    | "/api/operation.execute" ->
                                        let! body =
                                            (request.Content
                                             |> Option.ofObj
                                             |> Option.defaultWith (fun () ->
                                                 failtest "Expected request body"))
                                                .ReadAsStringAsync()

                                        bodies.Add(path, body)
                                        return reply (acceptedReply (operation body))
                                    | _ -> return failtestf "Unexpected path %s" path
                                })

                        use client = new HttpClient(handler)
                        use output = new MemoryStream()
                        use errors = new MemoryStream()

                        let access =
                            {
                                Configuration = configuration
                                Client = client
                                IsCurrent = (fun _ -> true)
                                Clock = clock
                                JournalDirectory = fun () -> Ok directory.FullName
                            }

                        use processor =
                            RemoteFrameProcessor.ForAuthenticatedTest(
                                {
                                    Input = Stream.Null
                                    Output = output
                                    Errors = errors
                                },
                                access,
                                token clock
                            )

                        let console =
                            {
                                IsInteractive = true
                                ReadLine =
                                    (fun () ->
                                        clock.Advance(TimeSpan.FromMinutes 10.)
                                        Some "yes")
                                Write = ignore
                            }

                        let args =
                            [
                                "case"
                                "open"
                                "LONG-REVIEW"
                                "--incident-date"
                                "2026-01-01"
                                "--incident-notification-date"
                                "2026-01-02"
                                "--incident-country"
                                "LV"
                                "--claimant-name"
                                "Synthetic claimant"
                                "--insurer-name"
                                "Synthetic insurer"
                                "--claimed-amount"
                                "10"
                                "--claimed-currency"
                                "EUR"
                            ]

                        let draft =
                            InteractiveReview.prepare console args |> required |> Option.get

                        let transport =
                            {
                                Exchange =
                                    (fun frame cancelled -> processor.Exchange(frame, cancelled))
                                Deliver = fun _ _ -> ()
                            }

                        let allocated = Guid.CreateVersion7(clock.GetUtcNow())
                        let mutable allocations = 0

                        let code =
                            InteractiveReview.run
                                (fun () ->
                                    allocations <- allocations + 1
                                    allocated)
                                console
                                transport
                                draft
                                CancellationToken.None
                            |> sync

                        Expect.equal code 0 "Accepted after deliberate long review"
                        Expect.equal allocations 1 "Only initial preparation allocates"

                        Expect.equal
                            (bodies |> Seq.map fst |> Seq.toList)
                            [ "/api/operation.preview"; "/api/operation.execute" ]
                            "One review and one execution"

                        Expect.equal
                            (snd bodies[0])
                            (snd bodies[1])
                            "Identical exact prepared request"

                        Expect.equal
                            (operation (snd bodies[1]))
                            allocated
                            "Previewed identity is not replaced after nine minutes"

                        Expect.isEmpty
                            (Directory.GetFiles directory.FullName)
                            "Confirmed acceptance cleans its one journal record"

                        let input =
                            Grammar.prepare
                                (fun () -> allocated.ToString("D"))
                                [
                                    "case"
                                    "close"
                                    "CASE-1"
                                    "--expected-revision"
                                    "1"
                                    "--operation-id"
                                    allocated.ToString("D")
                                ]
                            |> required
                            |> _.Invocation

                        let mutable replacements = 0

                        let allocate () =
                            replacements <- replacements + 1
                            Guid.CreateVersion7(clock.GetUtcNow())

                        Frames.refreshProvisional clock allocate (Frames.freeze input) |> ignore
                        Expect.equal replacements 0 "Frozen identity ignores age"

                        Frames.refreshProvisional clock allocate (Frames.provisional input)
                        |> ignore

                        Expect.equal
                            replacements
                            1
                            "Negative control detects genuinely provisional aged identity"
                    finally
                        directory.Delete(true))
            testCase
                "ID erasure confirms a reference from one installation and refuses a changed installation before execute"
                (fun () ->
                    let clock = Clock()
                    let mutable contexts = 0
                    let mutable previews = 0
                    let mutable executions = 0

                    use handler =
                        new CliTransportFixture.Handler(fun request ->
                            task {
                                let path =
                                    (request.RequestUri |> Option.ofObj |> Option.get)
                                        .AbsolutePath

                                match path with
                                | "/api/context" ->
                                    contexts <- contexts + 1

                                    return
                                        reply (
                                            contextReply
                                                (representation request)
                                                (if contexts = 1 then
                                                     installation
                                                 else
                                                     otherInstallation)
                                        )
                                | "/api/operation.preview" ->
                                    previews <- previews + 1

                                    let! body =
                                        (request.Content
                                         |> Option.ofObj
                                         |> Option.defaultWith (fun () ->
                                             failtest "Expected request body"))
                                            .ReadAsStringAsync()

                                    Expect.equal
                                        (request.Headers.GetValues("ClaimCore-Installation")
                                         |> Seq.exactlyOne)
                                        (installation.ToString("D"))
                                        "Review is installation-bound"

                                    return reply (previewReply (operation body) true)
                                | "/api/operation.execute" ->
                                    executions <- executions + 1
                                    return failtest "Changed installation must not execute"
                                | _ -> return failtestf "Unexpected path %s" path
                            })

                    use client = new HttpClient(handler)
                    use output = new MemoryStream()
                    use errors = new MemoryStream()

                    let access =
                        {
                            Configuration = configuration
                            Client = client
                            IsCurrent = (fun _ -> true)
                            Clock = clock
                            JournalDirectory =
                                fun () -> failtest "Mismatched installation must not journal"
                        }

                    use processor =
                        RemoteFrameProcessor.ForAuthenticatedTest(
                            {
                                Input = Stream.Null
                                Output = output
                                Errors = errors
                            },
                            access,
                            token clock
                        )

                    let console =
                        {
                            IsInteractive = false
                            ReadLine = (fun () -> None)
                            Write = ignore
                        }

                    let io =
                        {
                            Output = output
                            Errors = errors
                            Review = console
                        }

                    let code =
                        VerbRunner.run
                            io
                            processor
                            [
                                "case"
                                "erase"
                                "--case-id"
                                "10000000-0000-4000-8000-000000000001"
                                "--expected-revision"
                                "1"
                                "--expected-family-sequence"
                                "1"
                                "--confirm"
                                "CASE-1"
                            ]
                            CancellationToken.None
                        |> sync

                    Expect.equal contexts 2 "Fresh execution context is checked"
                    Expect.equal previews 1 "Reference comes from exactly one preview"
                    Expect.equal executions 0 "No retargeted execute HTTP"
                    Expect.equal code 3 "Visible installation mismatch refusal")
            testCase
                "copied read hints preserve every repeated view filter in supplied order"
                (fun () ->
                    let filter =
                        CaseListFilter.create
                            [ Lifecycle.Active; Lifecycle.Voided ]
                            [ CaseStatus.Opened; CaseStatus.Closed ]
                            [ CaseProgress.Decided; CaseProgress.PartlyPaid ]
                            None
                            CaseListOrder.Reference

                    let hint =
                        ControlHints.link (
                            Link.CaseList
                                {
                                    Page = { AfterCursor = None; Limit = 25 }
                                    Filter = filter
                                }
                        )

                    Expect.stringContains
                        hint
                        "--lifecycle ACTIVE --lifecycle VOIDED"
                        "Both lifecycle values"

                    Expect.stringContains
                        hint
                        "--status OPENED --status CLOSED"
                        "Both status values"

                    Expect.stringContains
                        hint
                        "--progress DECIDED --progress PARTLY_PAID"
                        "Both progress values"

                    let prepared =
                        ShellTokenizer.split hint
                        |> required
                        |> List.tail
                        |> Grammar.prepare (fun () ->
                            failtest "Read hints cannot allocate an operation")
                        |> required

                    let request = Frames.value Api.caseList prepared.Invocation |> Option.get

                    Expect.equal
                        request.Filter.Lifecycles
                        [ Lifecycle.Active; Lifecycle.Voided ]
                        "Read grammar keeps lifecycle selection"

                    Expect.equal
                        request.Filter.Statuses
                        [ CaseStatus.Opened; CaseStatus.Closed ]
                        "Read grammar keeps status selection"

                    Expect.equal
                        request.Filter.Progress
                        [ CaseProgress.Decided; CaseProgress.PartlyPaid ]
                        "Read grammar keeps progress selection")
            testCase
                "assignment preview hints contain deliberate update placeholders and keep unrelated seeds dormant"
                (fun () ->
                    let id =
                        CaseId.create (Guid.Parse "10000000-0000-4000-8000-000000000001")
                        |> Option.get

                    let revision = Revision.create 7L |> Option.get
                    let responsible = TaskAffordances.field BusinessField.ResponsibleActor
                    let summary = TaskAffordances.field BusinessField.HandlingSummary

                    let seeds =
                        Map.ofList
                            [
                                responsible, InputValue.Text "10000000-0000-4000-8000-000000000099"
                                summary, InputValue.Text "Private saved prose"
                            ]

                    let action = TaskAffordances.work id revision seeds [ responsible ]

                    let hint =
                        ControlHints.action (RenderingContext.forLanguage "en") action
                        |> String.concat "\n"

                    Expect.stringContains hint "--preview" "Copied ledger writes always preview"

                    Expect.stringContains
                        hint
                        "--responsible-actor '<responsibleActor>'"
                        "Assignment is deliberately editable"

                    Expect.stringContains
                        hint
                        "--expected-work-revision 7"
                        "Observed work version is exact"

                    Expect.isFalse
                        (hint.Contains "--handling-summary" || hint.Contains "Private saved prose")
                        "Unrelated update display seeds stay dormant"

                    Expect.isFalse
                        (hint.Contains "10000000-0000-4000-8000-000000000099")
                        "Seed is not an authored reassignment"

                    let summaryHint =
                        TaskAffordances.work id revision seeds [ summary ]
                        |> ControlHints.action (RenderingContext.forLanguage "en")
                        |> String.concat "\n"

                    Expect.stringContains
                        summaryHint
                        "--handling-summary '<handlingSummary>'"
                        "Summary update is explicit"

                    Expect.isFalse
                        (summaryHint.Contains "--responsible-actor")
                        "Unavailable assignment is not reasserted")
            testCase
                "read hint quoting preserves apostrophes and never emits raw terminal controls"
                (fun () ->
                    let hint =
                        ControlHints.link (
                            Link.CaseGet
                                {
                                    Target = CaseSelector.Reference "CASE'Q"
                                }
                        )

                    Expect.stringContains hint "'CASE'\\''Q'" "POSIX apostrophe quoting"

                    Expect.equal
                        (ShellTokenizer.split hint |> required |> List.last)
                        "CASE'Q"
                        "Exact reference spelling"

                    let unsafe =
                        ControlHints.link (
                            Link.CaseGet
                                {
                                    Target = CaseSelector.Reference "CASE\n\u001b[31m"
                                }
                        )

                    Expect.isFalse
                        (unsafe.Contains '\n' || unsafe.Contains '\u001b')
                        "Controls cannot become shell or terminal actions")

            testCase
                "preview dispatch freezes identity and its frame schema retains observed installation"
                (fun () ->
                    let clock = Clock()
                    let mutable journals = 0

                    use handler =
                        new CliTransportFixture.Handler(fun request ->
                            task {
                                let path =
                                    (request.RequestUri |> Option.ofObj |> Option.get)
                                        .AbsolutePath

                                if path = "/api/context" then
                                    return
                                        reply (contextReply (representation request) installation)
                                else
                                    Expect.equal
                                        path
                                        "/api/operation.preview"
                                        "Only a preview is dispatched"

                                    let! body =
                                        (request.Content |> Option.ofObj |> Option.get)
                                            .ReadAsStringAsync()

                                    return reply (previewReply (operation body) false)
                            })

                    use client = new HttpClient(handler)
                    use output = new MemoryStream()
                    use errors = new MemoryStream()

                    let access =
                        {
                            Configuration = configuration
                            Client = client
                            IsCurrent = (fun _ -> true)
                            Clock = clock
                            JournalDirectory =
                                fun () ->
                                    journals <- journals + 1
                                    Error()
                        }

                    use processor =
                        RemoteFrameProcessor.ForAuthenticatedTest(
                            {
                                Input = Stream.Null
                                Output = output
                                Errors = errors
                            },
                            access,
                            token clock
                        )

                    let frame =
                        CliTransportFixture.frame Api.operationPreview.Token
                        |> Frames.withRepresentation Representation.Human
                        |> Frames.provisional

                    let dispatched, response =
                        processor.ExchangePrepared(frame, CancellationToken.None) |> sync

                    Expect.equal response.ExitCode 0 "Review admitted"

                    Expect.isFalse
                        (Frames.isProvisional dispatched)
                        "First preview dispatch freezes identity"

                    Expect.equal journals 0 "Preview never opens mutation recovery"
                    let admitted = Frames.encodeInvocation dispatched |> Frames.admit |> required

                    Expect.equal
                        (Frames.installationId admitted)
                        (Some installation)
                        "Preview envelope preserves scope"

                    Expect.equal
                        (Frames.inputBytes admitted)
                        (Frames.inputBytes frame)
                        "Binding and freezing do not alter reviewed bytes")

            testCase
                "admission preview hints acquire the required grant rows through the existing argv grammar"
                (fun () ->
                    let task = ClientCompositions.admission

                    let seeds =
                        Map.ofList
                            [
                                TaskAffordances.input InputTarget.ProfileDisplayName,
                                InputValue.Text "Private display seed"
                            ]

                    let action =
                        TaskAffordances.offered
                            task
                            (Some(Binding.PeopleNew Revision.zero))
                            seeds
                            (TaskAffordances.authored task)
                            Map.empty

                    let hint =
                        ControlHints.action (RenderingContext.forLanguage "en") action
                        |> List.exactlyOne

                    let words = ShellTokenizer.split hint |> required

                    let rows =
                        words
                        |> List.pairwise
                        |> List.choose (fun (flag, value) ->
                            if flag = "--role" then Some value else None)

                    Expect.equal
                        rows
                        [ "<ROLE[@CASE-REF]>" ]
                        "Atomic admission requires one editable initial grant in the existing role/reference row syntax"

                    Expect.equal
                        (words |> List.last)
                        "--preview"
                        "Collection hints cannot become unreviewed writes"

                    Expect.stringContains
                        hint
                        "--role '<ROLE[@CASE-REF]>'"
                        "Row acquisition placeholder is shell-quoted"

                    Expect.isFalse
                        (hint.Contains "Private display seed" || hint.Contains "--operation-id")
                        "Private seeds and allocated identities do not enter rendering")

        ]
