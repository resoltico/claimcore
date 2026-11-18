module ClaimCore.Tests.TimelineOverviewTests

open System
open System.Text
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json
open ClaimCore.TestSupport

let private required =
    function
    | Ok value -> value
    | Error error -> failtestf "%A" error

let private id n =
    OperationIds.numberedAt (DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero)) n

let private action text = ReminderAction.parse text |> required
let private summary text = HandlingSummary.parse text |> required
let private date = CaseFixtures.date

let private primary =
    DomainInputs.namedActorReference (CaseFixtures.actorId (id 2)) "Current authored name"

let private originalId = ReminderId.create (id 10) |> Option.get

let private handling before after =
    TimelineChange.HandlingSummary { Before = before; After = after }

let private part description =
    {
        PartId = id 10
        Revision = Revision.first
        Description = description
    }

let private entry changes =
    {
        CaseTimelineEntry.OperationId = CaseFixtures.operationId (id 30)
        Position = EffectPosition.create Sequence.first 0 |> Option.get
        OperationHash = EntryHash.zero
        Subject =
            {
                PartKind = PartKind.Work
                PartId = id 10
                Revision = Revision.first
            }
        Task = TaskId.ofLiteral "work.update"
        RecordedAt = DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero)
        RecordedBy = primary
        Changes = changes
    }

let private encoded changes =
    Codec.encode TimelineCodecs.timelineEntry (entry changes)

[<Tests>]
let tests =
    testList
        "shared part timeline disclosure"
        [
            testCase
                "actor lookup rejects missing and mismatched identities and preserves current references"
                (fun () ->
                    let change =
                        WorkDescription.describe
                            None
                            (Some
                                {
                                    Responsible = Some primary.ActorId
                                    Summary = None
                                })
                        |> PartDescription.Work
                        |> part

                    Expect.isError (PartDescriptions.timeline Map.empty change) "Missing reference"

                    let impostor =
                        { primary with
                            ActorId = CaseFixtures.actorId (id 99)
                        }

                    Expect.isError
                        (PartDescriptions.timeline
                            (Map.ofList [ primary.ActorId, impostor ])
                            change)
                        "Wrong identity behind key"

                    for reference in
                        [
                            primary
                            { primary with
                                DisplayName = DisplayName.Erased
                            }
                        ] do
                        let disclosure =
                            PartDescriptions.timeline
                                (Map.ofList [ primary.ActorId, reference ])
                                change
                            |> required

                        Expect.equal
                            disclosure.Items
                            [
                                TimelineChange.Responsibility
                                    {
                                        Before = None
                                        After = Some reference
                                    }
                            ]
                            "Supplied current name or erasure")
            testCase
                "reminder due and state facets are independent of action differences"
                (fun () ->
                    let plan =
                        {
                            ReminderPlan.Action = action "Retained\u202eaction"
                            Due = date "2026-11-01"
                            Kind = ReminderKind.FollowUp
                        }

                    let before = ReminderValue.Available(plan, ReminderHandling.Pending)
                    let rescheduled = { plan with Due = date "2026-11-03" }
                    let complete = ReminderHandling.Completed(date "2026-11-02")

                    for after, facets in
                        [
                            ReminderValue.Available(plan, complete), [ TimelineFacet.DoneOn ]
                            ReminderValue.Available(rescheduled, ReminderHandling.Pending),
                            [ TimelineFacet.Due ]
                            ReminderValue.Available(rescheduled, complete),
                            [ TimelineFacet.Due; TimelineFacet.DoneOn ]
                        ] do
                        let description = ReminderDescription.describe (Some before) after

                        Expect.isNone
                            description.Action
                            "Unchanged retained action is not re-admitted or invented"

                        let disclosure =
                            PartDescriptions.timeline
                                Map.empty
                                (part (PartDescription.Reminder description))
                            |> required

                        Expect.equal
                            disclosure.Overview.Highlights
                            (if facets.IsEmpty then
                                 []
                             else
                                 [ TimelineHighlight.Reminder(0, facets) ])
                            "Due changes remain independent of unchanged action and event-supplied state"

                        if after.State = ReminderState.Completed then
                            Expect.equal
                                disclosure.Overview.Event
                                (TimelineEvent.ReminderCompleted before.Plan.Value.Action)
                                "Completion retains unchanged action and state meaning"

                        match disclosure.Items with
                        | [ TimelineChange.Reminder change ] ->
                            Expect.equal
                                change.State
                                (if before.State = after.State then
                                     None
                                 else
                                     Some
                                         {
                                             Before = Some before.State
                                             After = Some after.State
                                         })
                                "The complete state difference remains available independently of selected highlights"
                        | _ -> failtest "Reminder difference required")
            testCase
                "every closed event round-trips its fixed message and typed argument contract"
                (fun () ->
                    let first = action "First exact action"
                    let next = action "Next exact action"
                    let due = date "2024-02-29"

                    let events =
                        ([
                            PartKind.Case
                            PartKind.Payment
                            PartKind.Work
                            PartKind.Reminder
                            PartKind.Note
                         ]
                         |> List.map TimelineEvent.PartChanged)
                        @ [
                            TimelineEvent.ResponsibilityAssigned primary
                            TimelineEvent.ResponsibilityCleared
                                { primary with
                                    DisplayName = DisplayName.Erased
                                }
                            TimelineEvent.NoteRecorded false
                            TimelineEvent.NoteRecorded true
                            TimelineEvent.WorkRecorded
                            TimelineEvent.HandlingSummaryAdded
                            TimelineEvent.HandlingSummaryChanged
                            TimelineEvent.HandlingSummaryCleared
                            TimelineEvent.ReminderPlanned(first, due)
                            TimelineEvent.ReminderChanged first
                            TimelineEvent.ReminderCompleted first
                            TimelineEvent.ReminderCancelled first
                            TimelineEvent.ReminderReopened(first, due)
                            TimelineEvent.ReminderCompletedAndPlanned(first, next, due)
                        ]

                    let declared =
                        Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<TimelineEvent>)
                        |> Array.map _.Tag
                        |> Set.ofArray

                    let exercised =
                        events
                        |> List.map (fun value ->
                            Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(
                                value,
                                typeof<TimelineEvent>
                            )
                            |> fst
                            |> _.Tag)
                        |> Set.ofList

                    Expect.equal
                        exercised
                        declared
                        "Every semantic union case is represented, including erased-name context"

                    for event in events do
                        let description = TimelineEventPresentation.describe event

                        Expect.equal
                            (description.Arguments |> Map.keys |> Set.ofSeq)
                            TimelineEventPresentation.contracts[description.MessageId]
                            "Producer arguments match the published template contract"

                        let disclosure =
                            TimelineDisclosure.create [] { Event = event; Highlights = [] }
                            |> required

                        let original = entry (TimelineChanges.Disclosed disclosure)
                        let bytes = Codec.encode TimelineCodecs.timelineEntry original

                        Expect.equal
                            (Codec.decode Int32.MaxValue TimelineCodecs.timelineEntry bytes
                             |> required)
                            original
                            "Typed event values and fixed message ID round-trip exactly"

                        let node = JsonNode.Parse(Encoding.UTF8.GetString bytes) |> nonNull
                        let changes = node["changes"] |> nonNull
                        let overview = changes["overview"] |> nonNull
                        let encodedEvent = overview["event"] |> nonNull

                        Expect.equal
                            ((encodedEvent["messageId"] |> nonNull).GetValue<string>())
                            description.MessageId
                            "Codec message selection agrees with Core presentation"

                        encodedEvent["messageId"] <- JsonValue.Create "timeline.event.invalid"

                        Expect.isError
                            (Codec.decode
                                Int32.MaxValue
                                TimelineCodecs.timelineEntry
                                (Encoding.UTF8.GetBytes(node.ToJsonString())))
                            "A message cannot be substituted independently of its event")
            testCase
                "erased content cannot acquire an overview and payload-free disclosure rejects leaks"
                (fun () ->
                    for changes in [ TimelineChanges.Withheld; TimelineChanges.Erased ] do
                        let bytes = encoded changes

                        Expect.equal
                            (Codec.decode Int32.MaxValue TimelineCodecs.timelineEntry bytes
                             |> required)
                                .Changes
                            changes
                            "Payload-free alternatives round-trip"

                        let node = JsonNode.Parse(Encoding.UTF8.GetString bytes) |> nonNull

                        (node["changes"] |> nonNull)["overview"] <-
                            JsonNode.Parse(
                                """{"event":{"kind":"WORK_RECORDED","messageId":"timeline.event.workRecorded"},"highlights":[]}"""
                            )

                        Expect.isError
                            (Codec.decode
                                Int32.MaxValue
                                TimelineCodecs.timelineEntry
                                (Encoding.UTF8.GetBytes(node.ToJsonString())))
                            "Withheld and erased alternatives never admit overview data")
            testCase
                "native codec rejects malformed selectors and resolver preserves exact selected values"
                (fun () ->
                    let reminder =
                        TimelineChange.Reminder
                            {
                                ReminderId = originalId
                                Action =
                                    Some
                                        {
                                            Before = Some(action "Old")
                                            After = Some(action "New")
                                        }
                                Due = None
                                Kind = None
                                DoneOn = None
                                State =
                                    Some
                                        {
                                            Before = Some ReminderState.Pending
                                            After = Some ReminderState.Completed
                                        }
                            }

                    let items = [ handling None (Some(summary "Exact text")); reminder ]

                    let overview =
                        {
                            Event = TimelineEvent.WorkRecorded
                            Highlights =
                                [
                                    TimelineHighlight.Change 0
                                    TimelineHighlight.Reminder(1, [ TimelineFacet.Action ])
                                ]
                        }

                    let disclosure = TimelineDisclosure.create items overview |> required

                    match TimelineDisclosure.resolve disclosure |> required with
                    | [ _; TimelineChange.Reminder value ] ->
                        Expect.isNone value.State "Only explicit facets are projected"
                    | _ -> failtest "Expected two highlights"

                    let bytes = encoded (TimelineChanges.Disclosed disclosure)

                    for references in
                        [
                            """[{"kind":"CHANGE","index":-1}]"""
                            """[{"kind":"CHANGE","index":2}]"""
                            """[{"kind":"CHANGE","index":0},{"kind":"CHANGE","index":0}]"""
                            """[{"kind":"REMINDER","index":1,"facets":["ACTION"]},{"kind":"CHANGE","index":0}]"""
                            """[{"kind":"CHANGE","index":1}]"""
                            """[{"kind":"REMINDER","index":0,"facets":["ACTION"]}]"""
                            """[{"kind":"REMINDER","index":1,"facets":[]}]"""
                            """[{"kind":"REMINDER","index":1,"facets":["DUE"]}]"""
                            """[{"kind":"REMINDER","index":1,"facets":["STATE","ACTION"]}]"""
                            """[{"kind":"REMINDER","index":1,"facets":["ACTION","ACTION"]}]"""
                        ] do
                        let node = JsonNode.Parse(Encoding.UTF8.GetString bytes) |> nonNull

                        ((node["changes"] |> nonNull)["overview"] |> nonNull)["highlights"] <-
                            JsonNode.Parse references

                        Expect.isError
                            (Codec.decode
                                Int32.MaxValue
                                TimelineCodecs.timelineEntry
                                (Encoding.UTF8.GetBytes(node.ToJsonString())))
                            ("Malformed selector must fail: " + references)

                    Expect.isError
                        (TimelineDisclosure.resolve
                            { disclosure with
                                Overview =
                                    { overview with
                                        Highlights = [ TimelineHighlight.Change -1 ]
                                    }
                            })
                        "Direct negative index is an error, never an exception")
        ]
