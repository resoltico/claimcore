namespace ClaimCore.Domain

open System

[<Struct>]
type ReminderId = private ReminderId of Guid

module ReminderId =
    let create value =
        if value = Guid.Empty then None else Some(ReminderId value)

    let ofOperationId operation = ReminderId(OperationId.value operation)
    let value (ReminderId value) = value
    let format id = (value id).ToString("D")

[<RequireQualifiedAccess>]
type ReminderState =
    | Pending
    | Completed
    | Cancelled

module ReminderState =
    let vocabulary =
        Vocabulary.ofUnion<ReminderState> "reminderState" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

[<RequireQualifiedAccess>]
type ReminderKind =
    | FollowUp
    | Deadline

module ReminderKind =
    let vocabulary =
        Vocabulary.ofUnion<ReminderKind> "reminderKind" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary
    let defaultValue = ReminderKind.FollowUp

[<RequireQualifiedAccess>]
type ReminderKindSelection =
    | Any
    | Kind of ReminderKind

module ReminderKindSelection =
    let vocabulary =
        Vocabulary.define
            "reminderKindSelection"
            (ReminderKindSelection.Any
             :: (Vocabulary.all ReminderKind.vocabulary |> List.map ReminderKindSelection.Kind))
            (function
             | ReminderKindSelection.Any -> "ANY"
             | ReminderKindSelection.Kind kind -> ReminderKind.token kind)

    let kind =
        function
        | ReminderKindSelection.Any -> None
        | ReminderKindSelection.Kind kind -> Some kind

/// Query selection is distinct from the stored state: Any imposes no state constraint.
[<RequireQualifiedAccess>]
type ReminderStateSelection =
    | Any
    | State of ReminderState

module ReminderStateSelection =
    let vocabulary =
        Vocabulary.define
            "reminderStateSelection"
            (ReminderStateSelection.Any
             :: (ReminderState.all |> List.map ReminderStateSelection.State))
            (function
             | ReminderStateSelection.Any -> "ANY"
             | ReminderStateSelection.State state -> ReminderState.token state)

    let token = Vocabulary.token vocabulary

    let state =
        function
        | ReminderStateSelection.Any -> None
        | ReminderStateSelection.State state -> Some state

type ResolvedReminderSelection =
    {
        State: ReminderStateSelection
        IncludeInactive: bool
    }

/// Omission retains the public query default; explicit Any and false remain explicit inputs.
module ReminderSelection =
    let defaults hasView =
        {
            State =
                if hasView then
                    ReminderStateSelection.Any
                else
                    ReminderStateSelection.State ReminderState.Pending
            IncludeInactive = hasView
        }

    let resolve hasView state includeInactive =
        let basis = defaults hasView

        {
            State = Option.defaultValue basis.State state
            IncludeInactive = Option.defaultValue basis.IncludeInactive includeInactive
        }

type ReminderPlan =
    {
        Action: ReminderAction
        Due: CalendarDate
        Kind: ReminderKind
    }

[<RequireQualifiedAccess>]
type ReminderHandling =
    | Pending
    | Completed of doneOn: CalendarDate
    | Cancelled

module ReminderHandling =
    let state =
        function
        | ReminderHandling.Pending -> ReminderState.Pending
        | ReminderHandling.Completed _ -> ReminderState.Completed
        | ReminderHandling.Cancelled -> ReminderState.Cancelled

    let doneOn =
        function
        | ReminderHandling.Completed date -> Some date
        | ReminderHandling.Pending
        | ReminderHandling.Cancelled -> None

/// The ledger retains structural state even when the private compartment has been erased.
[<RequireQualifiedAccess>]
type ReminderValue =
    | Available of plan: ReminderPlan * handling: ReminderHandling
    | Unavailable of ReminderState

    member value.State =
        match value with
        | ReminderValue.Available(_, handling) -> ReminderHandling.state handling
        | ReminderValue.Unavailable state -> state

    member value.Plan =
        match value with
        | ReminderValue.Available(plan, _) -> Some plan
        | ReminderValue.Unavailable _ -> None

    member value.DoneOn =
        match value with
        | ReminderValue.Available(_, handling) -> ReminderHandling.doneOn handling
        | ReminderValue.Unavailable _ -> None

module ReminderValue =
    let pendingPlan =
        function
        | ReminderValue.Available(plan, ReminderHandling.Pending) -> Some plan
        | ReminderValue.Available _
        | ReminderValue.Unavailable _ -> None

    let ofPrivate state plan doneOn =
        match state, doneOn with
        | ReminderState.Pending, None -> Ok(ReminderValue.Available(plan, ReminderHandling.Pending))
        | ReminderState.Completed, Some date ->
            Ok(ReminderValue.Available(plan, ReminderHandling.Completed date))
        | ReminderState.Cancelled, None ->
            Ok(ReminderValue.Available(plan, ReminderHandling.Cancelled))
        | _ -> Error "A completion date is required only for a completed reminder."

/// Completion attribution belongs to the accepting ledger event, never caller input.
type Reminder =
    private
        {
            Id: ReminderId
            CaseId: CaseId
            Revision: Revision
            Plan: ReminderPlan
            Handling: ReminderHandling
        }

[<RequireQualifiedAccess>]
type ReminderRuleRefusal =
    | NotOffered of state: ReminderState
    | NoChanges

module Reminder =
    let id (value: Reminder) = value.Id
    let caseId (value: Reminder) = value.CaseId
    let revision (value: Reminder) = value.Revision
    let action (value: Reminder) = value.Plan.Action
    let due (value: Reminder) = value.Plan.Due
    let kind (value: Reminder) = value.Plan.Kind
    let state (value: Reminder) = ReminderHandling.state value.Handling
    let doneOn (value: Reminder) = ReminderHandling.doneOn value.Handling

    let ofAccepted id parent revision plan handling =
        if revision = Revision.zero then
            Error "A reminder version must be positive."
        else
            Ok
                {
                    Id = id
                    CaseId = parent
                    Revision = revision
                    Plan = plan
                    Handling = handling
                }

    let ofValue id parent revision =
        function
        | ReminderValue.Available(plan, handling) ->
            ofAccepted id parent revision plan handling |> Result.toOption
        | ReminderValue.Unavailable _ -> None

    let decode id parent revision action due kind state doneOn =
        ReminderValue.ofPrivate
            state
            {
                Action = action
                Due = due
                Kind = kind
            }
            doneOn
        |> Result.bind (function
            | ReminderValue.Available(plan, handling) -> ofAccepted id parent revision plan handling
            | ReminderValue.Unavailable _ -> Error "A reminder snapshot requires private content.")

    let columns snapshot =
        let action = snapshot |> Option.map action

        snapshot |> Option.map state,
        action,
        snapshot |> Option.map due,
        snapshot |> Option.map kind,
        snapshot |> Option.bind doneOn,
        action |> Option.map (ReminderAction.format >> TextSearch.normalize)

[<RequireQualifiedAccess>]
type DeadlineOutcome =
    | Overdue
    | Met
    | Missed
    | Cancelled

[<RequireQualifiedAccess>]
type DeadlineTiming =
    | PendingPastDue
    | FinishedByDue
    | FinishedAfterDue
    | Unrestricted

module DeadlineOutcome =
    let vocabulary =
        Vocabulary.ofUnion<DeadlineOutcome> "deadlineOutcome" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

    let plan =
        function
        | DeadlineOutcome.Overdue -> ReminderState.Pending, DeadlineTiming.PendingPastDue
        | DeadlineOutcome.Met -> ReminderState.Completed, DeadlineTiming.FinishedByDue
        | DeadlineOutcome.Missed -> ReminderState.Completed, DeadlineTiming.FinishedAfterDue
        | DeadlineOutcome.Cancelled -> ReminderState.Cancelled, DeadlineTiming.Unrestricted

    let ofTiming overdue reminder =
        if Reminder.kind reminder <> ReminderKind.Deadline then
            None
        else
            let state = Reminder.state reminder
            let due = Reminder.due reminder
            let doneOn = Reminder.doneOn reminder

            Vocabulary.all vocabulary
            |> List.tryFind (fun outcome ->
                let expected, date = plan outcome

                state = expected
                && (match date with
                    | DeadlineTiming.PendingPastDue -> overdue
                    | DeadlineTiming.FinishedByDue ->
                        doneOn |> Option.exists (fun value -> value <= due)
                    | DeadlineTiming.FinishedAfterDue ->
                        doneOn |> Option.exists (fun value -> value > due)
                    | DeadlineTiming.Unrestricted -> true))

[<RequireQualifiedAccess>]
type DateRelation =
    | Before
    | Same
    | After

[<RequireQualifiedAccess>]
type ReminderDue =
    | Overdue
    | Today
    | Upcoming

module ReminderDue =
    let vocabulary =
        Vocabulary.ofUnion<ReminderDue> "reminderDue" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

    let relation =
        function
        | ReminderDue.Overdue -> DateRelation.Before
        | ReminderDue.Today -> DateRelation.Same
        | ReminderDue.Upcoming -> DateRelation.After

    let matchesDate (today: DateOnly) value date =
        let comparison = compare (CalendarDate.value date) today

        match relation value with
        | DateRelation.Before -> comparison < 0
        | DateRelation.Same -> comparison = 0
        | DateRelation.After -> comparison > 0

    /// Current timing uses the owning read's business date; retained terminal/inactive work has none.
    let at (today: DateOnly) active reminder =
        if not active || Reminder.state reminder <> ReminderState.Pending then
            None
        else
            all
            |> List.tryFind (fun value -> matchesDate today value (Reminder.due reminder))
