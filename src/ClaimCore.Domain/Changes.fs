namespace ClaimCore.Domain

open System

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

[<RequireQualifiedAccess>]
type PaymentDifference =
    | Recorded of PaymentEntry
    | Removed of PaymentEntry

module PaymentDifference =
    let entry =
        function
        | PaymentDifference.Recorded entry
        | PaymentDifference.Removed entry -> entry

/// Financial changes retain entry identities and their before/after decision-currency context.
type PaymentCollectionDifference =
    {
        Entries: PaymentDifference list
        Before: PaymentTotals option
        After: PaymentTotals option
    }

/// Typed review facts retain their authoritative values independently of presentation snapshots.
type CaseReviewChanges =
    {
        Lifecycle: LifecycleChange option
        Fields: CaseFieldDifference list
        Payments: PaymentCollectionDifference option
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
    | Payments of PaymentCollectionDifference

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

[<RequireQualifiedAccess>]
type TimelineFacet =
    | Action
    | Due
    | State

module TimelineFacet =
    let vocabulary =
        Vocabulary.ofUnion<TimelineFacet> "timelineFacet" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

[<RequireQualifiedAccess>]
type TimelineHighlight =
    | Change of index: int
    | Reminder of index: int * facets: TimelineFacet list

type TimelineOverview =
    {
        Event: TimelineEvent
        Highlights: TimelineHighlight list
    }

type TimelineDisclosure =
    {
        Items: TimelineChange list
        Overview: TimelineOverview
    }

module TimelineDisclosure =
    let private reminderFacet (difference: ReminderDifference) =
        function
        | TimelineFacet.Action -> difference.Action.IsSome
        | TimelineFacet.Due -> difference.Due.IsSome
        | TimelineFacet.State -> difference.State.IsSome

    let private rank =
        function
        | TimelineFacet.Action -> 0
        | TimelineFacet.Due -> 1
        | TimelineFacet.State -> 2

    /// Canonical selectors are a bounded mechanical projection, never a second source of business values.
    let resolve (disclosure: TimelineDisclosure) =
        let index =
            function
            | TimelineHighlight.Change index
            | TimelineHighlight.Reminder(index, _) -> index

        let selectors = disclosure.Overview.Highlights

        let ordered =
            selectors
            |> List.pairwise
            |> List.forall (fun (before, after) -> index before < index after)

        let rec collect accumulated =
            function
            | [] -> Ok(List.rev accumulated)
            | selector :: rest ->
                match
                    selector,
                    (if index selector < 0 then
                         None
                     else
                         List.tryItem (index selector) disclosure.Items)
                with
                | TimelineHighlight.Change _, Some(TimelineChange.Reminder _) ->
                    Error "A reminder highlight requires explicit facets."
                | TimelineHighlight.Change _, Some value -> collect (value :: accumulated) rest
                | TimelineHighlight.Reminder(_, facets), Some(TimelineChange.Reminder difference) when
                    not facets.IsEmpty
                    && (facets
                        |> List.pairwise
                        |> List.forall (fun (before, after) -> rank before < rank after))
                    && (facets |> List.forall (reminderFacet difference))
                    ->
                    let value =
                        TimelineChange.Reminder
                            { difference with
                                Action =
                                    if List.contains TimelineFacet.Action facets then
                                        difference.Action
                                    else
                                        None
                                Due =
                                    if List.contains TimelineFacet.Due facets then
                                        difference.Due
                                    else
                                        None
                                State =
                                    if List.contains TimelineFacet.State facets then
                                        difference.State
                                    else
                                        None
                            }

                    collect (value :: accumulated) rest
                | _ ->
                    Error
                        "A timeline highlight must select an existing value and canonical nonempty facets."

        if not ordered then
            Error "Timeline highlight indexes must be strictly increasing."
        else
            collect [] selectors

    let create items (overview: TimelineOverview) =
        let disclosure: TimelineDisclosure = { Items = items; Overview = overview }
        resolve disclosure |> Result.map (fun _ -> disclosure)

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
        Disclosure: HistoryDisclosure
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
