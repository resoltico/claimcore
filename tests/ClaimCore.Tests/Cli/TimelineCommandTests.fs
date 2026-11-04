module ClaimCore.Tests.TimelineCommandTests

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Protocol
open ClaimCore.Cli
open ClaimCore.TestSupport

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
        Position =
            { CliReplyFixtures.receipt.Position with
                Sequence = CaseFixtures.sequence revision
            }
        Subject = TimelineSubject.CaseFact(CaseFixtures.revision revision, CaseActKind.Close)
        RecordedAt = DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero)
        RecordedBy = actor
        Changes = TimelineChanges.Withheld
    }

let private page revision next =
    {
        CaseTimelinePage.Basis = Inputs.workBasis
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

                    CliReply.result frame (Api.caseTimeline.Output.Write(pending.Dequeue()))
                    |> Fixtures.accepted
                    |> Task.FromResult
            Deliver =
                fun frame reply ->
                    let bytes =
                        if prepared.Format = VerbFormat.Json then
                            reply.Bytes
                        else
                            TextReply.bytesFor prepared.Language (Some frame) reply.Frame

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
                "retired human verbs refuse while explicit API frames remain available"
                (fun () ->
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
                            Expect.isSome
                                (Api.tryFind endpoint)
                                "Removing a human entry point does not remove explicit API access"

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
                "typed text shows current names exact money and explicit disclosure states"
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
                            Changes = TimelineChanges.Disclosed changes
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
                            run [ "--language"; language ] [ Outcome.Succeeded value ]

                        Expect.equal code 0 "Typed timeline text succeeds"

                        for expected in
                            [
                                "Timeline author"
                                TextReplyFormat.money language (Inputs.money "123456789.12" "EUR")
                                TextReplyFormat.money language (Inputs.money "0" "USD")
                                "2026-11-03"
                                "2026-11-04"
                                "First\\u{000A}second"
                            ] do
                            Expect.stringContains
                                text
                                expected
                                "Provided values survive thin formatting and safe escaping"

                        Expect.stringContains
                            text
                            (MessageCatalogs.renderCli language CliMessage.TimelineWithheld)
                            "Withheld changes have an explicit localized state"

                        Expect.stringContains
                            text
                            (MessageCatalogs.renderCli language CliMessage.TimelineErased)
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
