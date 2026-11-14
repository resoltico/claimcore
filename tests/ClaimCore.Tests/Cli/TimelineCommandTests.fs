module ClaimCore.Tests.TimelineCommandTests

open ClaimCore.CommandLine

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Presentation
open ClaimCore.Json
open ClaimCore.Cli
open ClaimCore.TestSupport

[<Tests>]
let optionalLimit =
    testCase "timeline admits omitted limits and materializes the shared default" (fun () ->
        let raw =
            Encoding.UTF8.GetBytes """{"target":{"kind":"REFERENCE","caseReference":"R-1"}}"""

        let decoded = Codec.decode 4096 Api.caseTimeline.Input raw |> Fixtures.accepted
        Expect.equal decoded.Limit 50 "Omission keeps the public default"

        let canonical =
            Codec.encode Api.caseTimeline.Input decoded |> Encoding.UTF8.GetString

        Expect.stringContains
            canonical
            "\"limit\":50"
            "CLI and browser assembly share explicit canonical paging")

let private actor =
    {
        ActorId = CaseFixtures.actorId CliReplyFixtures.id
        DisplayName = DisplayName.Named(Inputs.name "Timeline author")
        PrincipalKind = PrincipalKind.Human
        Availability = ActorAvailability.Available
    }

let private entry revision =
    {
        CaseTimelineEntry.OperationId = CaseFixtures.operationId CliReplyFixtures.id
        Position = EffectPosition.create (CaseFixtures.sequence revision) 0 |> Option.get
        OperationHash = CliReplyFixtures.receipt.Position.EntryHash
        Subject = TimelineSubject.CaseFact(CaseFixtures.revision revision, CaseActKind.Close)
        RecordedAt = DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero)
        RecordedBy = actor
        Changes = TimelineChanges.Withheld
    }

let private page revision next =
    {
        CaseTimelinePage.Disclosure = HistoryDisclosure.Full
        Basis = Inputs.workBasis
        CaseId = CliReplyFixtures.snapshot.CaseId
        Reference = CliReplyFixtures.snapshot.Reference
        Items = [ entry revision ]
        NextCursor = next
    }

let private run arguments (replies: Outcome<CaseTimelinePage> list) =
    let prepared =
        Grammar.prepare (fun () -> "unused") ([ "case"; "timeline"; "x" ] @ arguments)
        |> Fixtures.accepted

    let calls = ResizeArray<Invocation>()
    let pending = Collections.Generic.Queue<Outcome<CaseTimelinePage>>(replies)
    use output = new MemoryStream()
    use errors = new MemoryStream()

    let io =
        {
            Exchange =
                fun frame (_: CancellationToken) ->
                    calls.Add frame

                    CliReply.result
                        frame
                        (Api.caseTimeline.Output.Write(
                            let outcome = pending.Dequeue()

                            match Frames.representation frame with
                            | Representation.Human ->
                                HumanReplies.read HumanReadDocuments.timeline outcome
                                |> RepresentationReply.Human
                            | Representation.Data -> RepresentationReply.Data outcome
                        ))
                    |> Fixtures.accepted
                    |> Task.FromResult
            Deliver =
                fun frame reply ->
                    let bytes =
                        if prepared.Format = VerbFormat.Json then
                            reply.Bytes
                        else
                            ReplyText.bytesForWithDetails
                                prepared.Details
                                prepared.Language
                                (Some frame)
                                reply.Frame

                    output.Write bytes
                    output.WriteByte(byte '\n')
            Output = fun bytes -> output.Write(bytes: byte array)
            Errors = fun bytes -> errors.Write(bytes: byte array)
        }

    let code =
        WorkListCommand.run io prepared CancellationToken.None
        |> fun task -> task.GetAwaiter().GetResult()

    code,
    Encoding.UTF8.GetString(output.ToArray()),
    Encoding.UTF8.GetString(errors.ToArray()),
    List.ofSeq calls

[<Tests>]
let tests =
    testList
        "canonical timeline CLI"
        [
            testCase
                "CSV projects canonical typed money and retains disclosure through pagination"
                (fun () ->
                    let moneyChange =
                        TimelineChange.CaseMoney
                            {
                                FieldName = BusinessField.ClaimedAmount
                                Change =
                                    {
                                        Before = Some(Inputs.money "1234.5678" "EUR")
                                        After = Some(Inputs.money "100.0000" "EUR")
                                    }
                            }

                    let first =
                        { page 2L (Some "csv-next") with
                            Items =
                                [
                                    { entry 2L with
                                        Changes =
                                            TimelineChanges.Disclosed
                                                {
                                                    Items = [ moneyChange ]
                                                    Overview =
                                                        {
                                                            Event =
                                                                TimelineEvent.CaseActRecorded
                                                                    CaseActKind.Close
                                                            Highlights = []
                                                        }
                                                }
                                    }
                                ]
                        }

                    let last =
                        { page 1L None with
                            Items =
                                [
                                    { entry 1L with
                                        Changes = TimelineChanges.Erased
                                    }
                                ]
                        }

                    let code, text, errors, calls =
                        run
                            [ "--format"; "csv"; "--language"; "lv"; "--filter"; "facts"; "--all" ]
                            [ Outcome.Succeeded first; Outcome.Succeeded last ]

                    Expect.equal code 0 "CSV traversal succeeds"
                    Expect.equal errors "" "No false error or continuation remains at the end"
                    let records = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)

                    Expect.equal
                        records.Length
                        3
                        "One header and one row per supplied change/disclosure"

                    Expect.equal
                        (records
                         |> Array.filter (fun row -> row.StartsWith "caseReference,operationId")
                         |> Array.length)
                        1
                        "Header occurs once across all pages"

                    Expect.stringContains
                        text
                        "DISCLOSED,claimedAmount,1234.5678 EUR,100 EUR"
                        "Full Items survive empty overview highlights with canonical exact precision and no localized separators or imposed display zeros"

                    Expect.stringContains
                        text
                        "ERASED,,,"
                        "Erasure is explicit and carries no invented values"

                    Expect.equal calls.Length 2 "Continuation is followed"
                    let requests = calls |> List.map (Frames.value Api.caseTimeline >> Option.get)

                    Expect.equal
                        (requests |> List.map _.Filter)
                        [ Some TimelineFilter.Facts; Some TimelineFilter.Facts ]
                        "Filter never changes during CSV traversal"

                    Expect.equal
                        (requests |> List.map _.AfterCursor)
                        [ None; Some "csv-next" ]
                        "Only the continuation changes")
            testCase "each timeline entity keeps its authoritative act vocabulary" (fun () ->
                let revision = CaseFixtures.revision 1L

                let reminderId =
                    ReminderId.ofOperationId (CaseFixtures.operationId CliReplyFixtures.id)

                let subjects =
                    [
                        TimelineSubject.CaseFact(revision, CaseActKind.Close), "task", "case.close"
                        TimelineSubject.Work(revision, WorkActKind.UpdateWork),
                        "task",
                        "work.update"
                        TimelineSubject.Reminder(
                            ReminderActKind.Create,
                            reminderId,
                            revision,
                            None
                        ),
                        "task",
                        "reminder.create"
                    ]

                for subject, vocabulary, token in subjects do
                    let item = { entry 1L with Subject = subject }

                    let act =
                        TimelineScreen.timelineEntry item
                        |> List.head
                        |> _.Entries
                        |> List.find (fun entry -> entry.Key = "act")

                    Expect.equal
                        act.Value
                        (PresentedValue.Code(vocabulary, token))
                        "Work and reminder metadata cannot be relabelled as financial acts"

                    for language in LocalizationPolicy.languages do
                        let text =
                            ViewText.render language (TimelineScreen.rows [ item ])
                            |> String.concat "\n"

                        Expect.stringContains
                            text
                            (ReplyTextFormat.label
                                language
                                (PresentationKeys.code vocabulary token))
                            "Actual per-entity labels render in all supported languages")
            testCase "retired history and activity verbs and API endpoints refuse" (fun () ->
                for verb in [ "history"; "activity" ] do
                    match Grammar.prepare (fun () -> "unused") [ "case"; verb; "x" ] with
                    | Error [ UsageError.UnknownCommand(_, suggestions) ] ->
                        Expect.isFalse
                            (List.contains ("case " + verb) suggestions)
                            "Retired commands are never suggested as supported alternatives"
                    | other ->
                        failtestf
                            "Retired verb must be unknown: %A"
                            (Result.map (fun _ -> ()) other)

                    for endpoint in [ "case.history"; "case.activity" ] do
                        Expect.isNone
                            (Api.tryFind endpoint)
                            "Retired endpoints are absent from the API"

                Expect.equal
                    (ExitCode.value ExitCode.Usage)
                    64
                    "The executable usage exit code remains sixty-four"

                for operation in CommandGrammar.operations do
                    Expect.isFalse
                        (List.contains
                            (String.concat " " (CommandGrammar.words operation))
                            [ "case history"; "case activity" ])
                        "One command inventory excludes both retired verbs")
            testCase "global verb help lists only supported human commands" (fun () ->
                let help = ContractDiscovery.helpTopic "en" (CliTopic.Help HelpTopic.Verbs)

                Expect.stringContains
                    help
                    "claimcore case timeline"
                    "The canonical timeline command is discoverable"

                for verb in [ "history"; "activity" ] do
                    Expect.isFalse
                        (help.Contains("claimcore case " + verb))
                        "Retired commands are absent from the global verb list")
            testCase "timeline text pagination keeps technical entries behind details" (fun () ->
                for details in [ false; true ] do
                    let arguments = [ "--all" ] @ (if details then [ "--details" ] else [])

                    let code, text, errors, calls =
                        run
                            arguments
                            [
                                Outcome.Succeeded(page 2L (Some "second"))
                                Outcome.Succeeded(page 1L None)
                            ]

                    Expect.equal code 0 "Both timeline pages render successfully"
                    Expect.equal errors "" "No paging errors"
                    Expect.equal calls.Length 2 "The real page traversal follows the continuation"

                    Expect.stringContains
                        text
                        "Timeline author"
                        "Human attribution remains visible"

                    let operationId = OperationId.format (entry 1L).OperationId

                    Expect.equal
                        (text.Contains operationId)
                        details
                        "Technical operation identity is opt-in on every timeline page"

                    if details then
                        Expect.equal
                            (text.Split(operationId).Length - 1)
                            2
                            "Explicit details render the technical identity on both pages")
            testCase
                "all pages keep target filter and limit and emit exact JSON page frames"
                (fun () ->
                    let code, text, errors, calls =
                        run
                            [ "--all"; "--format"; "json"; "--filter"; "work"; "--limit"; "1" ]
                            [
                                Outcome.Succeeded(page 2L (Some "continuation"))
                                Outcome.Succeeded(page 1L None)
                            ]

                    Expect.equal code 0 "Traversal succeeds"
                    Expect.equal errors "" "No traversal errors"
                    Expect.equal calls.Length 2 "One exchange per core timeline page"

                    let requests =
                        calls
                        |> List.map (fun frame ->
                            Frames.value Api.caseTimeline frame |> Option.get)

                    Expect.equal
                        (requests |> List.map _.AfterCursor)
                        [ None; Some "continuation" ]
                        "Only the cursor advances"

                    for request in requests do
                        Expect.equal
                            request.Target
                            (CaseSelector.Reference "x")
                            "Target is unchanged"

                        Expect.equal
                            request.Filter
                            (Some TimelineFilter.Work)
                            "The selected core filter is unchanged"

                        Expect.equal request.Limit 1 "Page size is unchanged"

                    let lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    Expect.equal lines.Length 2 "JSON is one exact result frame per page"

                    for line in lines do
                        Expect.isOk
                            (Frames.decode (Encoding.UTF8.GetBytes line))
                            "Each emitted page remains a structured reply frame")
            testCase
                "typed text renders the supplied event table and explicit disclosure states"
                (fun () ->
                    let changes =
                        [
                            TimelineChange.CaseField
                                {
                                    FieldName = BusinessField.Status
                                    Before = Some(CaseFieldValue.Status CaseStatus.Opened)
                                    After = Some(CaseFieldValue.Status CaseStatus.Closed)
                                }
                            TimelineChange.CaseMoney
                                {
                                    FieldName = BusinessField.PayableAmount
                                    Change =
                                        {
                                            Before = Some(Inputs.money "123456789.12" "EUR")
                                            After = Some(Inputs.money "0" "USD")
                                        }
                                }
                            TimelineChange.Lifecycle
                                {
                                    Before = Some Lifecycle.Active
                                    After = Lifecycle.Voided
                                }
                            TimelineChange.Responsibility { Before = Some actor; After = None }
                            TimelineChange.HandlingSummary
                                {
                                    Before = None
                                    After =
                                        Some(
                                            HandlingSummary.parse "First\nsecond"
                                            |> Fixtures.accepted
                                        )
                                }
                            TimelineChange.Reminder
                                {
                                    ReminderId =
                                        ReminderId.create CliReplyFixtures.id |> Option.get
                                    Action = None
                                    Due =
                                        Some
                                            {
                                                Before = Some(Inputs.date "2026-11-03")
                                                After = Some(Inputs.date "2026-11-04")
                                            }
                                    State =
                                        Some
                                            {
                                                Before = Some ReminderState.Pending
                                                After = Some ReminderState.Completed
                                            }
                                }
                        ]

                    let disclosed =
                        { entry 2L with
                            Changes =
                                TimelineChanges.Disclosed
                                    {
                                        Items = changes
                                        Overview =
                                            {
                                                Event =
                                                    TimelineEvent.CaseActRecorded CaseActKind.Close
                                                Highlights = []
                                            }
                                    }
                        }

                    for language in [ "en"; "lv"; "ar" ] do
                        let value =
                            { page 2L None with
                                Items =
                                    [
                                        disclosed
                                        entry 1L
                                        { entry 1L with
                                            Changes = TimelineChanges.Erased
                                        }
                                    ]
                            }

                        let code, text, _, _ =
                            run [ "--language"; language; "--details" ] [ Outcome.Succeeded value ]

                        Expect.equal code 0 "Typed timeline text succeeds"

                        Expect.stringContains
                            text
                            (ReplyTextFormat.moneyWithScale
                                language
                                (Inputs.money "123456789.12" "EUR")
                                2)
                            "Exact old money survives the endpoint wire view."

                        Expect.stringContains
                            text
                            (ReplyTextFormat.moneyWithScale language (Inputs.money "0" "USD") 2)
                            "Exact new money survives."

                        Expect.stringContains
                            text
                            (ReplyTextFormat.date language (Inputs.date "2026-11-03"))
                            "Old date survives."

                        Expect.stringContains
                            text
                            (ReplyTextFormat.date language (Inputs.date "2026-11-04"))
                            "New date survives."

                        Expect.stringContains
                            text
                            "First\nsecond"
                            "Narrative paragraphs survive generic rendering."

                        Expect.stringContains
                            text
                            "Timeline author"
                            "Attribution is supplied by the table."

                        Expect.stringContains
                            text
                            (ReplyTextFormat.label language "task.case.close.label")
                            "The supplied act is localized."

                        Expect.stringContains
                            text
                            (ReplyTextFormat.label language "label.changesWithheld")
                            "Withheld changes have an explicit localized state"

                        Expect.stringContains
                            text
                            (ReplyTextFormat.label language "label.changesErased")
                            "Erased changes are distinguished"

                        Expect.isFalse
                            (text.Contains "{\"" || text.Contains "\"kind\"")
                            "Text does not fall back to a JSON object")
            testCase
                "single page and refused traversal stop without inventing a continuation"
                (fun () ->
                    let code, _, _, calls =
                        run
                            [ "--format"; "json" ]
                            [
                                Outcome.Succeeded(page 2L (Some "continuation"))
                                Outcome.Succeeded(page 1L None)
                            ]

                    Expect.equal code 0 "Single page succeeds"
                    Expect.equal calls.Length 1 "Without --all the next cursor is not followed"

                    let code, _, _, calls =
                        run
                            [ "--all"; "--format"; "json" ]
                            [
                                Outcome.Succeeded(page 2L (Some "continuation"))
                                Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable)
                            ]

                    Expect.equal code (ExitCode.value ExitCode.Rejected) "A refusal is preserved"
                    Expect.equal calls.Length 2 "A refused continuation ends traversal")
        ]
