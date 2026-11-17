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

/// Each accepted version is bounded independently of the number of other reminders.
/// Completion attribution belongs to the accepting ledger event, never caller input.
type Reminder =
    private
        {
            Id: ReminderId
            CaseId: CaseId
            Revision: Revision
            Action: ReminderAction
            Due: CalendarDate
            State: ReminderState
        }

type ReminderPlan =
    {
        Action: ReminderAction
        Due: CalendarDate
    }

[<RequireQualifiedAccess>]
type ReminderRuleRefusal =
    | NotOffered of state: ReminderState
    | NoChanges

/// A compound result is a fixed pair. Persistence must accept the whole result in one transaction.
module Reminder =
    let id (value: Reminder) = value.Id
    let caseId (value: Reminder) = value.CaseId
    let revision (value: Reminder) = value.Revision
    let action (value: Reminder) = value.Action
    let due (value: Reminder) = value.Due
    let state (value: Reminder) = value.State

    /// Retained decoding checks structural representability, not current admission.
    let ofAccepted id parent revision action due state =
        if revision = Revision.zero then
            Error "A reminder version must be positive."
        else
            Ok
                {
                    Id = id
                    CaseId = parent
                    Revision = revision
                    Action = action
                    Due = due
                    State = state
                }

    let columns snapshot =
        let action = snapshot |> Option.map action

        snapshot |> Option.map state,
        action,
        snapshot |> Option.map due,
        action |> Option.map (ReminderAction.format >> TextSearch.normalize)

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
