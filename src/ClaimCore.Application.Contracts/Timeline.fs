namespace ClaimCore.Application.Contracts

open System
open ClaimCore.Domain

[<RequireQualifiedAccess>]
type TimelineFilter =
    | Facts
    | Work

module TimelineFilter =
    let vocabulary =
        Vocabulary.ofUnion<TimelineFilter> "timelineFilter" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

type CaseTimelineRequest =
    {
        Target: CaseSelector
        Filter: TimelineFilter option
        AfterCursor: string option
        Limit: int
    }

type ValueChange<'value> =
    {
        Before: 'value option
        After: 'value option
    }

type ReminderDifference =
    {
        ReminderId: ReminderId
        Action: ValueChange<ReminderAction> option
        Due: ValueChange<CalendarDate> option
        State: ValueChange<ReminderState> option
    }

[<RequireQualifiedAccess>]
type TimelineChange =
    | CaseField of FieldDiff
    | Lifecycle of LifecycleChange
    | Responsibility of ValueChange<ActorRef>
    | HandlingSummary of ValueChange<HandlingSummary>
    | Reminder of ReminderDifference

[<RequireQualifiedAccess>]
type TimelineChanges =
    | Disclosed of TimelineChange list
    | Withheld
    | Erased

[<RequireQualifiedAccess>]
type TimelineSubject =
    | CaseFact of Revision * CaseActKind
    | Work of Revision * WorkActKind
    | Reminder of ReminderActKind * ReminderId * Revision * (ReminderId * Revision) option

type CaseTimelineEntry =
    {
        OperationId: OperationId
        Position: LedgerPosition
        Subject: TimelineSubject
        RecordedAt: DateTimeOffset
        RecordedBy: ActorRef
        Changes: TimelineChanges
    }

type CaseTimelinePage =
    {
        CaseId: CaseId
        Reference: CaseReference
        Items: CaseTimelineEntry list
        NextCursor: string option
    }
