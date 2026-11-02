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
    | ParentNotActive
    | ParentNotOpen
    | PendingRequired
    | TerminalRequired
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
            State = ReminderState.Pending
        }

    let private admit (plan: ReminderPlan) =
        ReminderAction.admit plan.Action
        |> Result.map (fun _ -> ())
        |> Result.mapError ReminderRefusal.InvalidAction

    let private pending reminder =
        if reminder.State = ReminderState.Pending then
            Ok()
        else
            Error ReminderRefusal.PendingRequired

    let private changed state (reminder: Reminder) =
        { reminder with
            State = state
            Revision = Revision.successor reminder.Revision
        }

    let private update reminder (plan: ReminderPlan) =
        pending reminder
        |> Result.bind (fun () -> if reminder.Action = plan.Action then Ok() else admit plan)
        |> Result.bind (fun () ->
            if reminder.Action = plan.Action && reminder.Due = plan.Due then
                Error ReminderRefusal.NoChanges
            else
                { changed ReminderState.Pending reminder with
                    Action = plan.Action
                    Due = plan.Due
                }
                |> ReminderEffects.Changed
                |> Ok)

    let private reopen reminder due =
        if reminder.State = ReminderState.Pending then
            Error ReminderRefusal.TerminalRequired
        else
            { changed ReminderState.Pending reminder with
                Due = due
            }
            |> ReminderEffects.Changed
            |> Ok

    let private completeAndPlan operation newIdReserved reminder plan =
        pending reminder
        |> Result.bind (fun () -> admit plan)
        |> Result.bind (fun () ->
            if newIdReserved || ReminderId.ofOperationId operation = reminder.Id then
                Error ReminderRefusal.IdentityReserved
            else
                ReminderEffects.CompletedAndPlanned(
                    changed ReminderState.Completed reminder,
                    create operation reminder.CaseId plan
                )
                |> Ok)

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
                pending reminder
                |> Result.map (fun () ->
                    changed ReminderState.Completed reminder |> ReminderEffects.Changed)
            | ReminderChange.Cancel ->
                pending reminder
                |> Result.map (fun () ->
                    changed ReminderState.Cancelled reminder |> ReminderEffects.Changed)
            | ReminderChange.Reopen due -> reopen reminder due
            | ReminderChange.Update plan -> update reminder plan
            | ReminderChange.CompleteAndPlanNext plan ->
                completeAndPlan operation newIdReserved reminder plan

    /// Pure decision over the actual locked parent/entity state. Authorization, replay and identity
    /// reservation are independent transaction safeguards; this result does not perform any writes.
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
                let isOpen = fields.Status = CaseStatus.Opened

                match command with
                | ReminderCommand.Create(_, plan) ->
                    if not isOpen then
                        Error ReminderRefusal.ParentNotOpen
                    elif newIdReserved then
                        Error ReminderRefusal.IdentityReserved
                    else
                        admit plan
                        |> Result.map (fun () ->
                            create operation parentId plan |> ReminderEffects.Created)
                | ReminderCommand.Change(_, id, expected, act) ->
                    match current with
                    | None -> Error ReminderRefusal.NotFound
                    | Some reminder when reminder.CaseId <> parentId || reminder.Id <> id ->
                        Error ReminderRefusal.NotFound
                    | Some reminder when reminder.Revision <> expected ->
                        Error(ReminderRefusal.RevisionConflict reminder.Revision)
                    | Some reminder -> change operation newIdReserved isOpen reminder act


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

type ReminderPlanInput = { Action: string; Due: string }

[<RequireQualifiedAccess>]
type AuthoredReminderAct =
    | Create of ReminderPlanInput
    | Update of reminderId: Guid * expectedRevision: int64 * ReminderPlanInput
    | Complete of reminderId: Guid * expectedRevision: int64
    | Cancel of reminderId: Guid * expectedRevision: int64
    | Reopen of reminderId: Guid * expectedRevision: int64 * due: string
    | CompleteAndPlanNext of reminderId: Guid * expectedRevision: int64 * ReminderPlanInput

module AuthoredReminderAct =
    let kind =
        function
        | AuthoredReminderAct.Create _ -> ReminderActKind.Create
        | AuthoredReminderAct.Update _ -> ReminderActKind.Update
        | AuthoredReminderAct.Complete _ -> ReminderActKind.Complete
        | AuthoredReminderAct.Cancel _ -> ReminderActKind.Cancel
        | AuthoredReminderAct.Reopen _ -> ReminderActKind.Reopen
        | AuthoredReminderAct.CompleteAndPlanNext _ -> ReminderActKind.CompleteAndPlanNext

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

module ReminderState =
    let vocabulary =
        Vocabulary.ofUnion<ReminderState> "reminderState" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

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
        | ReminderCommand.Create _ -> Revision.first
        | ReminderCommand.Change(_, _, revision, _) -> Revision.successor revision
