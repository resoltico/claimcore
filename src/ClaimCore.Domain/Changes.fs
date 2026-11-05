namespace ClaimCore.Domain

open System

type FieldDiff =
    {
        FieldName: BusinessField
        Before: string option
        After: string option
    }

type LifecycleChange =
    {
        Before: Lifecycle option
        After: Lifecycle
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

type CaseFieldDifference =
    {
        FieldName: BusinessField
        Before: CaseFieldValue option
        After: CaseFieldValue option
    }

/// Typed review facts retain their authoritative values independently of presentation snapshots.
type CaseReviewChanges =
    {
        Lifecycle: LifecycleChange option
        Fields: CaseFieldDifference list
    }

type CaseMoneyDifference =
    {
        FieldName: BusinessField
        Change: ValueChange<Money>
    }

[<RequireQualifiedAccess>]
type TimelineChange =
    | CaseField of CaseFieldDifference
    | CaseMoney of CaseMoneyDifference
    | Lifecycle of LifecycleChange
    | Responsibility of ValueChange<ActorRef>
    | HandlingSummary of ValueChange<HandlingSummary>
    | Reminder of ReminderDifference

/// A concise recorded event, not an instruction to perform an act.
[<RequireQualifiedAccess>]
type TimelineEvent =
    | CaseActRecorded of CaseActKind
    | ResponsibilityAssigned of ActorRef
    | ResponsibilityCleared of ActorRef
    | WorkRecorded
    | HandlingSummaryAdded
    | HandlingSummaryChanged
    | HandlingSummaryCleared
    | ReminderPlanned of action: ReminderAction * due: CalendarDate
    | ReminderChanged of ReminderAction
    | ReminderCompleted of ReminderAction
    | ReminderCancelled of ReminderAction
    | ReminderReopened of action: ReminderAction * due: CalendarDate
    | ReminderCompletedAndPlanned of
        completedAction: ReminderAction *
        nextAction: ReminderAction *
        nextDue: CalendarDate

type TimelineOverview =
    {
        Event: TimelineEvent
        Highlights: TimelineChange list
    }

type TimelineDisclosure =
    {
        Items: TimelineChange list
        Overview: TimelineOverview
    }

[<RequireQualifiedAccess>]
type TimelineChanges =
    | Disclosed of TimelineDisclosure
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

[<RequireQualifiedAccess>]
type ChangeEntity =
    | Case
    | Work
    | Reminder of ReminderId

/// A conflict continuation retains the observed upper revision, even if later changes are accepted.
type ChangeRange =
    {
        Entity: ChangeEntity
        ExpectedRevision: Revision
        ActualRevision: Revision
    }

type ChangePage =
    {
        Entries: CaseTimelineEntry list
        NextCursor: string option
    }

[<RequireQualifiedAccess>]
type ConflictChanges =
    | Withheld
    | Page of ChangePage

type RevisionConflictInfo =
    private
        {
            CurrentRevision: Revision
            History: ConflictChanges option
        }

    member this.ActualRevision = this.CurrentRevision
    member this.Changes = this.History

module RevisionConflictInfo =
    [<Literal>]
    let MaximumChanges = 50

    /// Pure decisions identify the revision; the authorized application snapshot supplies history.
    let at revision =
        {
            CurrentRevision = revision
            History = None
        }

    let explain changes conflict =
        match changes with
        | ConflictChanges.Page page when page.Entries.Length > MaximumChanges ->
            invalidArg (nameof changes) "A conflict page exceeds its admission budget."
        | _ -> { conflict with History = Some changes }
