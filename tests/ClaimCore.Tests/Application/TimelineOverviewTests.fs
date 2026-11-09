module ClaimCore.Tests.TimelineOverviewTests

open System
open System.Text
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Presentation
open ClaimCore.Json
open ClaimCore.TestSupport

let private required =
    function
    | Ok value -> value
    | Error problem -> failtestf "Fixture refused: %A" problem

let private id number =
    ClaimCore.TestSupport.OperationIds.numberedAt
        (DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero))
        number

let private operation number = CaseFixtures.operationId (id number)
let private parent = CaseFixtures.caseId (id 1)
let private date = CaseFixtures.date
let private action value = ReminderAction.parse value |> required
let private summary value = HandlingSummary.parse value |> required

let private person name =
    Inputs.namedActorReference (CaseFixtures.actorId (id 2)) name

let private primary = person "Current authored name"

let private responsibility before after =
    TimelineChange.Responsibility { Before = before; After = after }

let private handling before after =
    TimelineChange.HandlingSummary { Before = before; After = after }

let private workContent =
    EntryContent.Work(
        {
            OperationId = operation 3
            CaseId = parent
            ExpectedRevision = Revision.zero
            Update =
                {
                    Responsible = WorkValue.Set primary.ActorId
                    Summary = WorkValue.Keep
                }
        },
        {
            CaseId = parent
            Revision = Revision.first
            Work =
                {
                    Responsible = Some primary.ActorId
                    Summary = None
                }
        }
    )

let private work changes =
    TimelineOverview.create
        None
        (TimelineSubject.Work(Revision.first, WorkActKind.UpdateWork))
        workContent
        changes
    |> required

let private resolve event items highlights =
    TimelineDisclosure.create
        items
        {
            Event = event
            Highlights = highlights
        }
    |> required
    |> TimelineDisclosure.resolve
    |> required

let private plan value due : ReminderPlan =
    {
        Action = action value
        Due = date due
    }

let private originalRequest: ReminderRequestRecord =
    {
        OperationId = operation 10
        Command = ReminderCommand.Create(parent, plan "Original historical action" "2026-11-01")
    }

let private originalEffects =
    RecordedDecisions.reminder originalRequest None |> required

let private reminderValue =
    function
    | ReminderEffects.Created value
    | ReminderEffects.Changed value -> value
    | _ -> failtest "Single reminder required"

let private original = reminderValue originalEffects

let private originalContent =
    EntryContent.Reminder(originalRequest, originalEffects)

let private changed number change before =
    let request =
        {
            OperationId = operation number
            Command =
                ReminderCommand.Change(parent, Reminder.id before, Reminder.revision before, change)
        }

    request, RecordedDecisions.reminder request (Some before) |> required

let private projection scope request effects previous =
    let content = EntryContent.Reminder(request, effects)

    let current, next =
        match effects with
        | ReminderEffects.Created value
        | ReminderEffects.Changed value -> value, None
        | ReminderEffects.CompletedAndPlanned(value, next) ->
            value, Some(Reminder.id next, Reminder.revision next)

    let subject =
        TimelineSubject.Reminder(
            ReminderCommand.kind request.Command,
            Reminder.id current,
            Reminder.revision current,
            next
        )

    let changes =
        TimelineDifferences.describe (DateOnly(2026, 11, 4)) Map.empty content (Some previous)
        |> required

    let scoped =
        match scope with
        | Some(ChangeEntity.Reminder target) ->
            changes
            |> List.filter (function
                | TimelineChange.Reminder value -> value.ReminderId = target
                | _ -> false)
        | _ -> changes

    TimelineOverview.create scope subject content scoped |> required, scoped

let private entry changes =
    {
        CaseTimelineEntry.OperationId = operation 30
        Position =
            LedgerPosition.create
                Sequence.first
                (EntryHash.ofBytes (Array.zeroCreate 32) |> Option.get)
        Subject = TimelineSubject.Work(Revision.first, WorkActKind.UpdateWork)
        RecordedAt = DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero)
        RecordedBy = primary
        Changes = changes
    }

let private encoded changes =
    Codec.encode TimelineCodecs.timelineEntry (entry changes)

let private scalar field value =
    TimelineChange.CaseField
        {
            FieldName = field
            Before = None
            After = Some value
        }

[<Tests>]
let tests =
    testList
        "Core timeline overviews"
        [
            testCase "timeline actor maps reject missing and mismatched identities" (fun () ->
                Expect.equal
                    (TimelineDifferences.describe
                        (DateOnly(2026, 11, 4))
                        Map.empty
                        workContent
                        None)
                    (Error CoreFault.StoreIntegrityError)
                    "Missing reference is an integrity fault"

                let impostor =
                    { primary with
                        ActorId = CaseFixtures.actorId (id 99)
                        DisplayName = DisplayName.Named(CaseFixtures.name "Wrong actor name")
                    }

                Expect.equal
                    (TimelineDifferences.describe
                        (DateOnly(2026, 11, 4))
                        (Map.ofList [ primary.ActorId, impostor ])
                        workContent
                        None)
                    (Error CoreFault.StoreIntegrityError)
                    "A map key must not disguise another actor's identity or name")
            testCase
                "timeline author and responsibility lookups retain valid renamed and erased refs"
                (fun () ->
                    for reference in
                        [
                            { primary with
                                DisplayName =
                                    DisplayName.Named(CaseFixtures.name "Current renamed person")
                            }
                            { primary with
                                DisplayName = DisplayName.Erased
                            }
                        ] do
                        let references = Map.ofList [ primary.ActorId, reference ]

                        Expect.equal
                            (TimelineDifferences.actor references primary.ActorId)
                            (Ok reference)
                            "The author and responsibility paths share identity-checked lookup"

                        let changes =
                            TimelineDifferences.describe
                                (DateOnly(2026, 11, 4))
                                references
                                workContent
                                None
                            |> required

                        Expect.equal
                            (work changes).Event
                            (TimelineEvent.ResponsibilityAssigned reference)
                            "Overview cannot invent, cache or substitute a different name")
            testCase
                "case act highlight policy is exhaustive and keeps exact full differences"
                (fun () ->
                    let items =
                        [
                            scalar
                                BusinessField.PaymentDate
                                (CaseFieldValue.Business(Admitted.Date(date "2026-11-01")))
                            TimelineChange.CaseMoney
                                {
                                    FieldName = BusinessField.PayableAmount
                                    Change =
                                        {
                                            Before = Some(CaseFixtures.money "100" "JPY")
                                            After = Some(CaseFixtures.money "1234.5678" "EUR")
                                        }
                                }
                            scalar
                                BusinessField.ClaimantName
                                (CaseFieldValue.Business(
                                    Admitted.Name(CaseFixtures.name "Typed party")
                                ))
                        ]

                    let fields =
                        {
                            CaseFields.CaseReference = CaseFixtures.reference "OVERVIEW"
                            Status = CaseStatus.Opened
                            Registration = Inputs.admittedRegistration Inputs.registration
                            Finance = FinancialPosition.undecided
                        }

                    let snapshot =
                        {
                            CaseId = parent
                            Reference = fields.CaseReference
                            Revision = Revision.first
                            State = CaseState.Active fields
                        }

                    for authored, _ in
                        Inputs.caseActs
                            Inputs.registration
                            Inputs.decision
                            Inputs.payment
                            "Recorded reason" do
                        let kind = CaseAct.kind authored

                        let command =
                            CaseFixtures.command
                                {
                                    OperationId = id 4
                                    CaseReference = "OVERVIEW"
                                    ExpectedRevision = if kind = CaseActKind.Open then 0L else 1L
                                    Act = authored
                                }

                        let result =
                            TimelineOverview.create
                                None
                                (TimelineSubject.CaseFact(Revision.first, kind))
                                (EntryContent.Case(
                                    command,
                                    Some { After = snapshot; Payment = None }
                                ))
                                items
                            |> required

                        let expected =
                            match kind with
                            | CaseActKind.AmendRegistration
                            | CaseActKind.Correct ->
                                [
                                    TimelineHighlight.Change 0
                                    TimelineHighlight.Change 1
                                    TimelineHighlight.Change 2
                                ]
                            | CaseActKind.Decide
                            | CaseActKind.WithdrawDecision -> [ TimelineHighlight.Change 1 ]
                            | CaseActKind.RecordPayment
                            | CaseActKind.RemovePayment -> []
                            | _ -> []

                        Expect.equal
                            result.Event
                            (TimelineEvent.CaseActRecorded kind)
                            "The Core supplies a recorded event, never an imperative renderer label"

                        Expect.equal
                            result.Highlights
                            expected
                            "Semantic field policy does not rank or cap by length"

                        let disclosure = TimelineDisclosure.create items result |> required
                        Expect.equal disclosure.Items items "Full differences remain untouched"

                        Expect.isOk
                            (TimelineDisclosure.resolve disclosure)
                            "Every selected reference resolves")
            testCase
                "assignment and clearing use current supplied actor refs and only summary highlights"
                (fun () ->
                    let text = summary "Exact multiline\nالعربية Latviešu"
                    let items = [ responsibility None (Some primary); handling None (Some text) ]
                    let assigned = work items

                    Expect.equal
                        assigned.Event
                        (TimelineEvent.ResponsibilityAssigned primary)
                        "Authored current actor reference is used"

                    Expect.equal
                        assigned.Highlights
                        [ TimelineHighlight.Change 1 ]
                        "The person is already in the sentence"

                    Expect.equal
                        (resolve assigned.Event items assigned.Highlights)
                        [ handling None (Some text) ]
                        "Summary remains exact"

                    let erased =
                        { primary with
                            DisplayName = DisplayName.Erased
                        }

                    Expect.equal
                        (work [ responsibility (Some erased) None ]).Event
                        (TimelineEvent.ResponsibilityCleared erased)
                        "Erased names stay erased; no historical or IdP fallback")
            testCase
                "summary added replaced cleared and unchanged work have truthful events"
                (fun () ->
                    let before = summary "Before"
                    let after = summary "After"

                    for values, event in
                        [
                            [ handling None (Some after) ], TimelineEvent.HandlingSummaryAdded
                            [ handling (Some before) (Some after) ],
                            TimelineEvent.HandlingSummaryChanged
                            [ handling (Some before) None ], TimelineEvent.HandlingSummaryCleared
                            [], TimelineEvent.WorkRecorded
                        ] do
                        Expect.equal
                            (work values).Event
                            event
                            "Typed absent sides determine Core event meaning"

                    Expect.isError
                        (TimelineOverview.create
                            (Some ChangeEntity.Case)
                            (TimelineSubject.Work(Revision.first, WorkActKind.UpdateWork))
                            workContent
                            [])
                        "An entity range cannot be silently reinterpreted")
            testCase
                "complete and cancel retain historical action absent from their state-only differences"
                (fun () ->
                    for change, expected in
                        [
                            ReminderChange.Complete,
                            TimelineEvent.ReminderCompleted(Reminder.action original)
                            ReminderChange.Cancel,
                            TimelineEvent.ReminderCancelled(Reminder.action original)
                        ] do
                        let request, effects = changed 11 change original
                        let result, items = projection None request effects originalContent

                        Expect.equal
                            result.Event
                            expected
                            "Unchanged action comes from validated historical effects"

                        Expect.equal
                            result.Highlights
                            []
                            "State transition is already expressed by the event"

                        match items with
                        | [ TimelineChange.Reminder difference ] ->
                            Expect.isNone difference.Action "No action delta exists to reconstruct"
                        | _ -> failtestf "Unexpected retained differences: %A" items)
            testCase
                "update and reopen select explicit non-null facets in canonical order"
                (fun () ->
                    let request, effects =
                        changed
                            12
                            (ReminderChange.Update(plan "Changed action" "2026-11-02"))
                            original

                    let result, items = projection None request effects originalContent

                    Expect.equal
                        result.Event
                        (TimelineEvent.ReminderChanged(action "Changed action"))
                        "After action identifies the historical reminder"

                    Expect.equal
                        result.Highlights
                        [
                            TimelineHighlight.Reminder(
                                0,
                                [ TimelineFacet.Action; TimelineFacet.Due ]
                            )
                        ]
                        "State is not invented"

                    Expect.equal
                        (resolve result.Event items result.Highlights)
                        items
                        "Both changed facets remain exact"

                    let completedRequest, completedEffects =
                        changed 13 ReminderChange.Complete original

                    let completed = reminderValue completedEffects

                    let reopenedRequest, reopenedEffects =
                        changed 14 (ReminderChange.Reopen(date "2026-11-03")) completed

                    let reopened, full =
                        projection
                            None
                            reopenedRequest
                            reopenedEffects
                            (EntryContent.Reminder(completedRequest, completedEffects))

                    Expect.equal
                        reopened.Event
                        (TimelineEvent.ReminderReopened(
                            Reminder.action original,
                            date "2026-11-03"
                        ))
                        "Reopen retains unchanged action"

                    Expect.equal
                        reopened.Highlights
                        [ TimelineHighlight.Reminder(0, [ TimelineFacet.Due ]) ]
                        "The Core selects only the due facet"

                    match resolve reopened.Event full reopened.Highlights with
                    | [ TimelineChange.Reminder difference ] ->
                        Expect.isNone
                            difference.State
                            "Resolver cannot leak an unselected state facet"
                    | _ -> failtest "Expected one resolved reminder")
            testCase
                "paired reminder overviews obey original and next conflict ranges before exposing context"
                (fun () ->
                    let request, effects =
                        changed
                            15
                            (ReminderChange.CompleteAndPlanNext(
                                plan "Private next action" "2026-11-04"
                            ))
                            original

                    let next =
                        match effects with
                        | ReminderEffects.CompletedAndPlanned(_, next) -> next
                        | _ -> failtest "Expected paired effects"

                    let ordinary, all = projection None request effects originalContent

                    Expect.equal
                        ordinary.Event
                        (TimelineEvent.ReminderCompletedAndPlanned(
                            Reminder.action original,
                            action "Private next action",
                            date "2026-11-04"
                        ))
                        "Ordinary timeline represents both effects"

                    Expect.equal all.Length 2 "Full ordinary differences include both effects"

                    let originalView, originalItems =
                        projection
                            (Some(ChangeEntity.Reminder(Reminder.id original)))
                            request
                            effects
                            originalContent

                    let nextView, nextItems =
                        projection
                            (Some(ChangeEntity.Reminder(Reminder.id next)))
                            request
                            effects
                            originalContent

                    Expect.equal
                        originalView.Event
                        (TimelineEvent.ReminderCompleted(Reminder.action original))
                        "Original range excludes next-action context"

                    Expect.equal
                        nextView.Event
                        (TimelineEvent.ReminderPlanned(
                            action "Private next action",
                            date "2026-11-04"
                        ))
                        "Next range excludes original context"

                    Expect.equal originalItems.Length 1 "Scope precedes reference indexing"
                    Expect.equal nextItems.Length 1 "Each scope has its own index space"

                    let wire overview items =
                        TimelineDisclosure.create items overview
                        |> required
                        |> TimelineChanges.Disclosed
                        |> encoded
                        |> Encoding.UTF8.GetString

                    Expect.isFalse
                        ((wire originalView originalItems)
                            .Contains("Private next action", StringComparison.Ordinal))
                        "Other effect text is absent from the entire payload"

                    Expect.isFalse
                        ((wire nextView nextItems)
                            .Contains("Original historical action", StringComparison.Ordinal))
                        "Original effect text is absent from next-only payload")
            testCase "retained action is not re-admitted under modern authoring rules" (fun () ->
                let oldAction = action "Retained\u202eaction"

                Expect.isError
                    (ReminderAction.admit oldAction)
                    "Negative control is not currently authorable"

                let old =
                    Reminder.ofAccepted
                        (Reminder.id original)
                        parent
                        Revision.first
                        oldAction
                        (Reminder.due original)
                        ReminderState.Pending
                    |> required

                let oldRequest =
                    { originalRequest with
                        Command =
                            ReminderCommand.Create(
                                parent,
                                {
                                    Action = oldAction
                                    Due = Reminder.due original
                                }
                            )
                    }

                let request, effects = changed 16 ReminderChange.Complete old

                let overview, _ =
                    projection
                        None
                        request
                        effects
                        (EntryContent.Reminder(oldRequest, ReminderEffects.Created old))

                Expect.equal
                    overview.Event
                    (TimelineEvent.ReminderCompleted oldAction)
                    "Permanent retained value survives exactly")
            testCase
                "every closed event round-trips its fixed message and typed argument contract"
                (fun () ->
                    let first = action "First exact action"
                    let next = action "Next exact action"
                    let due = date "2024-02-29"

                    let events =
                        (CaseActKind.all |> List.map TimelineEvent.CaseActRecorded)
                        @ [
                            TimelineEvent.ResponsibilityAssigned primary
                            TimelineEvent.ResponsibilityCleared
                                { primary with
                                    DisplayName = DisplayName.Erased
                                }
                            TimelineEvent.NoteRecorded NoteActKind.AddNote
                            TimelineEvent.NoteRecorded NoteActKind.RemoveNote
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
                    Expect.isError
                        (TimelineOverview.create
                            None
                            (TimelineSubject.CaseFact(Revision.first, CaseActKind.Erase))
                            EntryContent.ErasedCase
                            [])
                        "Erased pipeline must bypass context projection"

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
                                ReminderId = Reminder.id original
                                Action =
                                    Some
                                        {
                                            Before = Some(action "Old")
                                            After = Some(action "New")
                                        }
                                Due = None
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
