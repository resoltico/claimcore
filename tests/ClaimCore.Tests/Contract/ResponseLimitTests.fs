module ClaimCore.Tests.ResponseLimitTests

open ClaimCore.Wire
open ClaimCore.Presentation

open ClaimCore.TestSupport

open ClaimCore.Json

open System
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

let private id = Guid.Parse "d0000000-0000-7000-8000-000000000001"
let private reference = String('"', 80)
let private instant = DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero)
let private maximum = 9007199254740991L

let private fields length : CaseFields =
    {
        CaseReference = CaseFixtures.reference reference
        Status = CaseStatus.Closed
        Registration =
            CaseFixtures.registration (
                ClaimCore.TestSupport.Inputs.registrationWith
                    [
                        BusinessField.ClaimantName, (String(char 1, length))
                        BusinessField.InsurerName, (String(char 1, length))
                        BusinessField.ClaimedAmount, ("999999999999999999.9999")
                    ]
                    (Inputs.registration)
            )
        Progress = ClaimCore.TestSupport.CaseFixtures.progress (None) (None)
    }

let private snapshot length : CaseSnapshot =
    {
        CaseId = CaseFixtures.caseId (System.Guid.Parse "20000000-0000-7000-8000-000000000001")
        Reference = CaseFixtures.reference reference
        Revision = CaseFixtures.revision maximum
        State = CaseState.Voided(fields length)
    }

let private receipt disclosure : OperationReceipt =
    {
        OperationId = (CaseFixtures.operationId (id))
        Position =
            {
                Sequence = CaseFixtures.sequence maximum
                EntryHash =
                    (String('a', 64)
                     |> System.Convert.FromHexString
                     |> EntryHash.ofBytes
                     |> Option.get)
            }
        RecordedAt = instant
        Basis =
            {
                ObservedAt = instant
                BusinessDate = DateOnly.FromDateTime instant.UtcDateTime
                TimeZoneId = "Etc/UTC"
            }
        RecordedBy = ClaimCore.TestSupport.Inputs.actorReference ((CaseFixtures.actorId (id)))
        Subject =
            ReceiptSubject.Case
                {
                    CaseId =
                        CaseFixtures.caseId (
                            System.Guid.Parse "20000000-0000-7000-8000-000000000001"
                        )
                    Reference = CaseFixtures.reference reference
                    Revision = CaseFixtures.revision maximum
                    Act = CaseActKind.AmendRegistration
                    Snapshot = disclosure
                }
    }

let private authorityFixture () =
    let prefix = "https://issuer.example/"

    let principal =
        PrincipalKey.human (prefix + String('+', 2048 - prefix.Length)) (String(char 1, 512))
        |> Result.defaultWith (string >> failwith)

    let actor: Actor =
        {
            ActorId = (CaseFixtures.actorId (id))
            Principal = principal
            Enabled = false
            Profile = None
        }

    let grant: ScopedGrant =
        {
            Role = Role.PaymentRecorder
            Scope = GrantScope.Case(CaseFixtures.caseId id, CaseFixtures.reference reference)
        }

    actor, grant

let private boundedAuthority () =
    let actor, grant = authorityFixture ()

    let actors count =
        Codec.encode
            Api.authorityList.Output
            (Outcome.Succeeded(
                Presented.create
                    (fun (page: ActorReviewPage) ->
                        Descriptor.tableView Descriptor.actorColumns page.Items)
                    {
                        Basis = Inputs.workBasis
                        Items = List.replicate count (ActorReviewItem.ofActor [] actor)
                        NextCursor = Some(String('A', Cursors.MaximumTokenCharacters))
                    }
            ))

    let grants count =
        Codec.encode
            Api.authorityGrants.Output
            (Outcome.Succeeded(
                Presented.create
                    (fun (page: AuthorityGrantPage) ->
                        Descriptor.tableView Descriptor.grantColumns page.Items)
                    {
                        Basis = Inputs.workBasis
                        Actor = ActorReviewItem.ofActor [] actor
                        Items = List.replicate count grant
                        NextCursor = Some(String('A', Cursors.MaximumTokenCharacters))
                    }
            ))

    Expect.isLessThan
        (actors 50).Length
        ClaimCore.Wire.Limits.MaximumResponseBytes
        "Maximal admitted actor headers fit"

    Expect.isLessThan
        (grants 50).Length
        ClaimCore.Wire.Limits.MaximumResponseBytes
        "Maximal scoped grants and target header fit"

    Expect.throws (fun () -> actors 51 |> ignore) "Actor overproduction refuses, never truncates"
    Expect.throws (fun () -> grants 51 |> ignore) "Grant overproduction refuses, never truncates"

let private timelineLimit () =
    let required =
        function
        | Ok value -> value
        | Error problem -> failtestf "Budget fixture must be representable: %A" problem

    let person number character =
        {
            ActorId =
                CaseFixtures.actorId (
                    ClaimCore.TestSupport.OperationIds.numberedAt
                        (DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero))
                        number
                )
            DisplayName =
                DisplayName.Named(
                    PartyName.parse (String(character, PartyName.MaximumBytes)) |> required
                )
            PrincipalKind = PrincipalKind.Human
            Availability = ActorAvailability.Available
        }

    let changes =
        [
            TimelineChange.Responsibility
                {
                    Before = Some(person 2 (char 2))
                    After = Some(person 3 (char 3))
                }
            TimelineChange.HandlingSummary
                {
                    Before =
                        Some(
                            HandlingSummary.parse (String(char 4, HandlingSummary.MaximumBytes))
                            |> required
                        )
                    After =
                        Some(
                            HandlingSummary.parse (String(char 5, HandlingSummary.MaximumBytes))
                            |> required
                        )
                }
        ]

    let entry =
        {
            CaseTimelineEntry.OperationId = CaseFixtures.operationId id
            Position = (receipt SnapshotDisclosure.Withheld).Position
            Subject = TimelineSubject.Work(CaseFixtures.revision maximum, WorkActKind.UpdateWork)
            RecordedAt = instant
            RecordedBy = person 1 (char 1)
            Changes =
                TimelineChanges.Disclosed
                    {
                        Items = changes
                        Overview =
                            {
                                Event = TimelineEvent.ResponsibilityAssigned(person 3 (char 3))
                                Highlights = [ TimelineHighlight.Change 1 ]
                            }
                    }
        }

    let bytes =
        Codec.encode
            Api.caseTimeline.Output
            (Outcome.Succeeded(
                Presented.create
                    (fun (page: CaseTimelinePage) ->
                        Descriptor.tableView Descriptor.timelineColumns page.Items)
                    {
                        CaseTimelinePage.Basis = Inputs.workBasis
                        CaseId = CaseFixtures.caseId id
                        Reference = CaseFixtures.reference reference
                        Items = List.replicate Paging.MaximumPageSize entry
                        NextCursor = Some(String('A', Cursors.MaximumTokenCharacters))
                    }
            ))

    Expect.isLessThan
        bytes.Length
        Limits.MaximumResponseBytes
        "The complete reply with its view fits the 4 MiB transport cap."

    Expect.isOk
        (Codec.decode Limits.MaximumResponseBytes Api.caseTimeline.Output bytes)
        "The actual maximal page decodes under the public limit"

let private reminderTimelineLimit () =
    let required =
        function
        | Ok value -> value
        | Error problem -> failtestf "Budget fixture refused: %A" problem

    let actor =
        {
            ActorRef.ActorId = CaseFixtures.actorId id
            DisplayName =
                DisplayName.Named(
                    PartyName.parse (String(char 1, PartyName.MaximumBytes)) |> required
                )
            PrincipalKind = PrincipalKind.Human
            Availability = ActorAvailability.Available
        }

    let oldAction =
        ReminderAction.parse (String(char 2, ReminderAction.MaximumBytes)) |> required

    let nextAction =
        ReminderAction.parse (String(char 3, ReminderAction.MaximumBytes)) |> required

    let first = ReminderId.ofOperationId (CaseFixtures.operationId id)

    let next =
        ReminderId.ofOperationId (
            CaseFixtures.operationId (Guid.Parse "d0000000-0000-7000-8000-000000000001")
        )

    let due = CaseFixtures.date "2026-11-05"

    let updated =
        TimelineChange.Reminder
            {
                ReminderId = first
                Action =
                    Some
                        {
                            Before = Some oldAction
                            After = Some nextAction
                        }
                Due = Some { Before = Some due; After = Some due }
                State = None
            }

    let completed =
        TimelineChange.Reminder
            {
                ReminderId = first
                Action = None
                Due = None
                State =
                    Some
                        {
                            Before = Some ReminderState.Pending
                            After = Some ReminderState.Completed
                        }
            }

    let planned =
        TimelineChange.Reminder
            {
                ReminderId = next
                Action =
                    Some
                        {
                            Before = None
                            After = Some nextAction
                        }
                Due = Some { Before = None; After = Some due }
                State =
                    Some
                        {
                            Before = None
                            After = Some ReminderState.Pending
                        }
            }

    for kind, event, changes, highlights, version in
        [
            ReminderActKind.Update,
            TimelineEvent.ReminderChanged nextAction,
            [ updated ],
            [ TimelineHighlight.Reminder(0, [ TimelineFacet.Action; TimelineFacet.Due ]) ],
            None
            ReminderActKind.CompleteAndPlanNext,
            TimelineEvent.ReminderCompletedAndPlanned(oldAction, nextAction, due),
            [ completed; planned ],
            [],
            Some(next, Revision.first)
        ] do
        let entry =
            {
                CaseTimelineEntry.OperationId = CaseFixtures.operationId id
                Position = (receipt SnapshotDisclosure.Withheld).Position
                Subject = TimelineSubject.Reminder(kind, first, Revision.first, version)
                RecordedAt = instant
                RecordedBy = actor
                Changes =
                    TimelineDisclosure.create
                        changes
                        {
                            Event = event
                            Highlights = highlights
                        }
                    |> required
                    |> TimelineChanges.Disclosed
            }

        let reply =
            Outcome.Succeeded
                {
                    CaseTimelinePage.Basis = Inputs.workBasis
                    CaseId = CaseFixtures.caseId id
                    Reference = CaseFixtures.reference reference
                    Items = List.replicate Paging.MaximumPageSize entry
                    NextCursor = None
                }

        let bytes =
            Codec.encode
                Api.caseTimeline.Output
                (Presented.outcome
                    (fun (page: CaseTimelinePage) ->
                        Descriptor.tableView Descriptor.timelineColumns page.Items)
                    reply)

        Expect.isLessThan
            bytes.Length
            Limits.MaximumResponseBytes
            "The complete reminder timeline reply with its view fits the transport cap."

        Expect.isOk
            (Codec.decode Limits.MaximumResponseBytes Api.caseTimeline.Output bytes)
            "Both maximal fifty-entry reminder pages decode"

[<Tests>]
let tests =
    testList
        "bounded response codecs"
        [
            testCase "authority headers and grants have actual bounded codecs" boundedAuthority
            testCase "fifty typed timeline differences fit the response limit" timelineLimit
            testCase
                "fifty maximal reminder event contexts fit alongside full differences"
                reminderTimelineLimit
        ]
