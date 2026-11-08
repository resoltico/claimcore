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

[<RequireQualifiedAccess>]
type ReminderActKind =
    | Create
    | Update
    | Complete
    | Cancel
    | Reopen
    | CompleteAndPlanNext

module ReminderActKind =
    let vocabulary =
        Vocabulary.ofUnion<ReminderActKind> "reminderAct" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

module ReminderState =
    let vocabulary =
        Vocabulary.ofUnion<ReminderState> "reminderState" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

    /// Retained structural transitions do not depend on current parent or permission policy.
    let after act previous =
        match act, previous with
        | ReminderActKind.Create, None -> Some ReminderState.Pending
        | ReminderActKind.Update, Some ReminderState.Pending -> Some ReminderState.Pending
        | (ReminderActKind.Complete | ReminderActKind.CompleteAndPlanNext),
          Some ReminderState.Pending -> Some ReminderState.Completed
        | ReminderActKind.Cancel, Some ReminderState.Pending -> Some ReminderState.Cancelled
        | ReminderActKind.Reopen, Some(ReminderState.Completed | ReminderState.Cancelled) ->
            Some ReminderState.Pending
        | _ -> None

    /// A disclosed result may omit its predecessor while retaining its act's structural state.
    let isResultOf act state =
        None :: (all |> List.map Some)
        |> List.exists (fun before -> after act before = Some state)

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
type ReminderChange =
    | Update of ReminderPlan
    | Complete
    | Cancel
    | Reopen of due: CalendarDate
    | CompleteAndPlanNext of ReminderPlan

/// A creation has no existing entity revision. Changes always identify their parent and version.
[<RequireQualifiedAccess>]
type ReminderCommand =
    | Create of parent: CaseId * ReminderPlan
    | Change of parent: CaseId * ReminderId * expected: Revision * ReminderChange

[<RequireQualifiedAccess>]
type ReminderRefusal =
    | ParentMismatch
    | ParentNotActive
    | ParentNotOpen
    | NotFound
    | RevisionConflict of actual: Revision
    | IdentityReserved
    | PendingRequired
    | TerminalRequired
    | NoChanges
    | InvalidAction of Violation

/// Public business-rule refusals exclude identity privacy, revision and input boundary failures.
[<RequireQualifiedAccess>]
type ReminderRuleRefusal =
    | NotOffered of state: ReminderState
    | NoChanges

/// A compound result is a fixed pair. Persistence must accept the whole result in one transaction.
[<RequireQualifiedAccess>]
type ReminderEffects =
    | Created of Reminder
    | Changed of Reminder
    | CompletedAndPlanned of completed: Reminder * next: Reminder

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

    let private create operation parent plan =
        {
            Id = ReminderId.ofOperationId operation
            CaseId = parent
            Revision = Revision.first
            Action = plan.Action
            Due = plan.Due
            State = ReminderState.after ReminderActKind.Create None |> Option.get
        }

    let private admit (plan: ReminderPlan) =
        ReminderAction.admit plan.Action
        |> Result.map (fun _ -> ())
        |> Result.mapError ReminderRefusal.InvalidAction

    let private nextState act reminder =
        ReminderState.after act (Some reminder.State)
        |> Option.map Ok
        |> Option.defaultValue (
            Error(
                if act = ReminderActKind.Reopen then
                    ReminderRefusal.TerminalRequired
                else
                    ReminderRefusal.PendingRequired
            )
        )

    let private changed state (reminder: Reminder) =
        { reminder with
            State = state
            Revision = Revision.successor reminder.Revision
        }

    let private update reminder (plan: ReminderPlan) =
        nextState ReminderActKind.Update reminder
        |> Result.bind (fun state ->
            (if reminder.Action = plan.Action then Ok() else admit plan)
            |> Result.bind (fun () ->
                if reminder.Action = plan.Action && reminder.Due = plan.Due then
                    Error ReminderRefusal.NoChanges
                else
                    { changed state reminder with
                        Action = plan.Action
                        Due = plan.Due
                    }
                    |> ReminderEffects.Changed
                    |> Ok))

    let private reopen reminder due =
        nextState ReminderActKind.Reopen reminder
        |> Result.map (fun state ->
            { changed state reminder with
                Due = due
            }
            |> ReminderEffects.Changed)

    let private completeAndPlan operation newIdReserved reminder plan =
        nextState ReminderActKind.CompleteAndPlanNext reminder
        |> Result.bind (fun state ->
            admit plan
            |> Result.bind (fun () ->
                if newIdReserved || ReminderId.ofOperationId operation = reminder.Id then
                    Error ReminderRefusal.IdentityReserved
                else
                    ReminderEffects.CompletedAndPlanned(
                        changed state reminder,
                        create operation reminder.CaseId plan
                    )
                    |> Ok))

    let private change operation newIdReserved isOpen reminder act =
        let openRequired =
            match act with
            | ReminderChange.Complete
            | ReminderChange.Cancel -> false
            | _ -> true

        if openRequired && not isOpen then
            Error ReminderRefusal.ParentNotOpen
        else
            match act with
            | ReminderChange.Complete ->
                nextState ReminderActKind.Complete reminder
                |> Result.map (fun state -> changed state reminder |> ReminderEffects.Changed)
            | ReminderChange.Cancel ->
                nextState ReminderActKind.Cancel reminder
                |> Result.map (fun state -> changed state reminder |> ReminderEffects.Changed)
            | ReminderChange.Reopen due -> reopen reminder due
            | ReminderChange.Update plan -> update reminder plan
            | ReminderChange.CompleteAndPlanNext plan ->
                completeAndPlan operation newIdReserved reminder plan

    /// Entity effects share one decision; the parent decision supplies open/closed policy.
    let decideEffects operation parentId isOpen current newIdReserved command =
        match command with
        | ReminderCommand.Create(_, plan) ->
            if not isOpen then
                Error ReminderRefusal.ParentNotOpen
            elif newIdReserved then
                Error ReminderRefusal.IdentityReserved
            else
                admit plan
                |> Result.map (fun () -> create operation parentId plan |> ReminderEffects.Created)
        | ReminderCommand.Change(_, id, expected, act) ->
            match current with
            | None -> Error ReminderRefusal.NotFound
            | Some reminder when reminder.CaseId <> parentId || reminder.Id <> id ->
                Error ReminderRefusal.NotFound
            | Some reminder when reminder.Revision <> expected ->
                Error(ReminderRefusal.RevisionConflict reminder.Revision)
            | Some reminder -> change operation newIdReserved isOpen reminder act

    /// Pure decision over the actual locked parent/entity state.
    let decide operation (parent: CaseSnapshot) current newIdReserved command =
        let parentId =
            match command with
            | ReminderCommand.Create(parent, _)
            | ReminderCommand.Change(parent, _, _, _) -> parent

        if parentId <> parent.CaseId then
            Error ReminderRefusal.ParentMismatch
        else
            match parent.State with
            | CaseState.Voided _
            | CaseState.Erased -> Error ReminderRefusal.ParentNotActive
            | CaseState.Active fields ->
                decideEffects
                    operation
                    parentId
                    (fields.Status = CaseStatus.Opened)
                    current
                    newIdReserved
                    command


type ReminderPlanInput = { Action: string; Due: string }

[<RequireQualifiedAccess>]
type ReminderAct<'revision> =
    | Create of ReminderPlanInput
    | Update of reminderId: Guid * expectedRevision: 'revision * ReminderPlanInput
    | Complete of reminderId: Guid * expectedRevision: 'revision
    | Cancel of reminderId: Guid * expectedRevision: 'revision
    | Reopen of reminderId: Guid * expectedRevision: 'revision * due: string
    | CompleteAndPlanNext of reminderId: Guid * expectedRevision: 'revision * ReminderPlanInput

type AuthoredReminderAct = ReminderAct<int64>

module ReminderAct =
    let kind =
        function
        | ReminderAct.Create _ -> ReminderActKind.Create
        | ReminderAct.Update _ -> ReminderActKind.Update
        | ReminderAct.Complete _ -> ReminderActKind.Complete
        | ReminderAct.Cancel _ -> ReminderActKind.Cancel
        | ReminderAct.Reopen _ -> ReminderActKind.Reopen
        | ReminderAct.CompleteAndPlanNext _ -> ReminderActKind.CompleteAndPlanNext

module AuthoredReminderAct =
    let kind (act: AuthoredReminderAct) = ReminderAct.kind act

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

    /// Current timing uses the owning read's business date; retained terminal/inactive work has none.
    let at (today: DateOnly) active reminder =
        if not active || Reminder.state reminder <> ReminderState.Pending then
            None
        elif CalendarDate.value (Reminder.due reminder) < today then
            Some ReminderDue.Overdue
        elif CalendarDate.value (Reminder.due reminder) = today then
            Some ReminderDue.Today
        else
            Some ReminderDue.Upcoming

module ReminderCommand =
    let parent =
        function
        | ReminderCommand.Create(parent, _)
        | ReminderCommand.Change(parent, _, _, _) -> parent

    let kind =
        function
        | ReminderCommand.Create _ -> ReminderActKind.Create
        | ReminderCommand.Change(_, _, _, change) ->
            match change with
            | ReminderChange.Update _ -> ReminderActKind.Update
            | ReminderChange.Complete -> ReminderActKind.Complete
            | ReminderChange.Cancel -> ReminderActKind.Cancel
            | ReminderChange.Reopen _ -> ReminderActKind.Reopen
            | ReminderChange.CompleteAndPlanNext _ -> ReminderActKind.CompleteAndPlanNext

    let target operation =
        function
        | ReminderCommand.Create _ -> ReminderId.ofOperationId operation
        | ReminderCommand.Change(_, id, _, _) -> id

    let resultingRevision =
        function
        | ReminderCommand.Create _ -> Some Revision.first
        | ReminderCommand.Change(_, _, revision, _) when Revision.value revision < Revision.Maximum ->
            Some(Revision.successor revision)
        | ReminderCommand.Change _ -> None

    let matchesEffects operation command effects =
        let parent = parent command
        let id = target operation command
        let revision = resultingRevision command

        let primary value =
            Reminder.caseId value = parent
            && Reminder.id value = id
            && Some(Reminder.revision value) = revision

        let planMatches (plan: ReminderPlan) value =
            Reminder.action value = plan.Action && Reminder.due value = plan.Due

        let stateMatches act value =
            ReminderState.isResultOf act (Reminder.state value)

        match command, effects with
        | ReminderCommand.Create(_, plan), ReminderEffects.Created value ->
            primary value && planMatches plan value && stateMatches (kind command) value
        | ReminderCommand.Change(_, _, _, ReminderChange.Update plan), ReminderEffects.Changed value ->
            primary value && planMatches plan value && stateMatches (kind command) value
        | ReminderCommand.Change(_, _, _, ReminderChange.Complete), ReminderEffects.Changed value ->
            primary value && stateMatches ReminderActKind.Complete value
        | ReminderCommand.Change(_, _, _, ReminderChange.Cancel), ReminderEffects.Changed value ->
            primary value && stateMatches ReminderActKind.Cancel value
        | ReminderCommand.Change(_, _, _, ReminderChange.Reopen due), ReminderEffects.Changed value ->
            primary value && stateMatches (kind command) value && Reminder.due value = due
        | ReminderCommand.Change(_, _, _, ReminderChange.CompleteAndPlanNext plan),
          ReminderEffects.CompletedAndPlanned(completed, next) ->
            primary completed
            && stateMatches ReminderActKind.CompleteAndPlanNext completed
            && Reminder.id next = ReminderId.ofOperationId operation
            && Reminder.id next <> Reminder.id completed
            && Reminder.caseId next = parent
            && Reminder.revision next = Revision.first
            && ReminderState.after ReminderActKind.Create None = Some(Reminder.state next)
            && planMatches plan next
        | _ -> false
