namespace ClaimCore.Domain

open System

type ReminderSubjectState =
    {
        ReminderId: ReminderId
        CaseId: CaseId
        Revision: Revision
        Status: ReminderState option
        Snapshot: Reminder option
        RecordedAt: DateTimeOffset
        RecordedBy: ActorId
        Erased: bool
    }

type ReminderSubjectStructure =
    {
        Stamp: SubjectStamp
        CaseId: CaseId
        ReminderId: ReminderId
        Revision: Revision
        Act: ReminderActKind
        NextReminderId: ReminderId option
    }

type ReminderSubjectDependencies =
    {
        Parent: CaseSubjectState
        IdentityReserved: bool
    }

type ReminderSubjectRows =
    {
        ReminderId: ReminderId
        CaseId: CaseId
        Revision: Revision
        Snapshot: Reminder option
        RecordedAt: DateTimeOffset
        RecordedBy: ActorId
        Status: ReminderState option
        Action: ReminderAction option
        Due: CalendarDate option
        SearchText: string option
    }

module ReminderSubject =
    let private refusal (parent: CaseSubjectState) (current: ReminderSubjectState option) =
        function
        | ReminderRefusal.IdentityReserved -> Refusal.IdempotencyConflict
        | ReminderRefusal.RevisionConflict revision -> Refusal.revisionConflict revision
        | ReminderRefusal.InvalidAction violation ->
            Refusal.invalidInput (InputTarget.ReminderAction, violation)
        | ReminderRefusal.NotFound
        | ReminderRefusal.ParentMismatch -> Refusal.Access AccessRefusal.ResourceUnavailable
        | ReminderRefusal.ParentNotActive
        | ReminderRefusal.ParentNotOpen ->
            let headline =
                match parent.Lifecycle with
                | Lifecycle.Erased -> CaseHeadline.Erased
                | Lifecycle.Voided -> CaseHeadline.Voided
                | Lifecycle.Active -> CaseHeadline.Closed

            Refusal.Case(CaseRefusal.NotOffered headline)
        | ReminderRefusal.PendingRequired
        | ReminderRefusal.TerminalRequired ->
            current
            |> Option.bind _.Status
            |> Option.map (fun status -> Refusal.Reminder(ReminderRuleRefusal.NotOffered status))
            |> Option.defaultValue (Refusal.Access AccessRefusal.ResourceUnavailable)
        | ReminderRefusal.NoChanges -> Refusal.Reminder ReminderRuleRefusal.NoChanges

    let decide operation dependencies (state: ReminderSubjectState option) command =
        match dependencies.Parent.Snapshot with
        | None -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)
        | Some parent ->
            Reminder.decide
                operation
                parent
                (state |> Option.bind _.Snapshot)
                dependencies.IdentityReserved
                command
            |> Result.mapError (refusal dependencies.Parent state)

    /// One compound decision yields the primary and newly planned entity states together.
    let apply
        (recordedAt: DateTimeOffset)
        (recordedBy: ActorId)
        (_state: ReminderSubjectState option)
        effects
        =
        let values =
            match effects with
            | ReminderEffects.Created value
            | ReminderEffects.Changed value -> [ value ]
            | ReminderEffects.CompletedAndPlanned(completed, next) -> [ completed; next ]

        values
        |> List.map (fun value ->
            {
                ReminderId = Reminder.id value
                CaseId = Reminder.caseId value
                Revision = Reminder.revision value
                Status = Some(Reminder.state value)
                Snapshot = Some value
                RecordedAt = recordedAt
                RecordedBy = recordedBy
                Erased = false
            })

    let applyErased
        (dependencies: ReminderSubjectDependencies)
        (state: ReminderSubjectState option)
        (effect: ReminderSubjectStructure)
        =
        let parent = dependencies.Parent
        let expected = state |> Option.map _.Revision |> Option.defaultValue Revision.zero
        let creating = effect.Act = ReminderActKind.Create

        let terminal =
            effect.Act = ReminderActKind.Complete || effect.Act = ReminderActKind.Cancel

        let nextStatus = ReminderState.after effect.Act (state |> Option.bind _.Status)

        let valid =
            parent.Lifecycle = Lifecycle.Active
            && parent.CaseId = effect.CaseId
            && (terminal || parent.Status = CaseStatus.Opened)
            && creating = state.IsNone
            && Revision.value expected < Revision.Maximum
            && effect.Revision = Revision.successor expected
            && (state
                |> Option.forall (fun state ->
                    state.ReminderId = effect.ReminderId && state.CaseId = effect.CaseId))
            && nextStatus.IsSome
            && not dependencies.IdentityReserved
            && ((effect.NextReminderId.IsSome) = (effect.Act = ReminderActKind.CompleteAndPlanNext))
            && (not creating
                || effect.ReminderId = ReminderId.ofOperationId effect.Stamp.OperationId)
            && (effect.NextReminderId
                |> Option.forall (fun id ->
                    id = ReminderId.ofOperationId effect.Stamp.OperationId
                    && id <> effect.ReminderId))

        if not valid then
            Error RecordViolation.InvalidFinancialPosition
        else
            let primary =
                {
                    ReminderId = effect.ReminderId
                    CaseId = effect.CaseId
                    Revision = effect.Revision
                    Status = nextStatus
                    Snapshot = None
                    RecordedAt = effect.Stamp.RecordedAt
                    RecordedBy = effect.Stamp.ActorId
                    Erased = false
                }

            let next =
                effect.NextReminderId
                |> Option.map (fun id ->
                    { primary with
                        ReminderId = id
                        Revision = Revision.first
                        Status = Some ReminderState.Pending
                    })
                |> Option.toList

            Ok(primary :: next)

    let erase (state: ReminderSubjectState) =
        { state with
            Snapshot = None
            Erased = true
        }

    let columns snapshot =
        let action = snapshot |> Option.map Reminder.action

        snapshot |> Option.map Reminder.state,
        action,
        snapshot |> Option.map Reminder.due,
        action |> Option.map (ReminderAction.format >> TextSearch.normalize)

    let rows (state: ReminderSubjectState) =
        if state.Snapshot.IsNone && not state.Erased then
            None
        else
            let status, action, due, search = columns state.Snapshot

            Some
                {
                    ReminderId = state.ReminderId
                    CaseId = state.CaseId
                    Revision = state.Revision
                    Snapshot = state.Snapshot
                    RecordedAt = state.RecordedAt
                    RecordedBy = state.RecordedBy
                    Status = status
                    Action = action
                    Due = due
                    SearchText = search
                }
