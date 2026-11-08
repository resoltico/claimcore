namespace ClaimCore.RecordFormat

open ClaimCore.Domain

/// Permanent effects exclude today's text admission. These functions also serve causal verification;
/// a valid encoding or matching hash does not establish that the accepted business effects agree.
module HistoricalWorkEffects =
    let work (request: WorkRequestRecord) (before: CaseWorkSnapshot) =
        let after = CaseWork.apply request.Update before.Work

        if
            request.CaseId <> before.CaseId
            || request.ExpectedRevision <> before.Revision
            || Revision.value before.Revision = Revision.Maximum
            || after = before.Work
        then
            Error "Invalid retained work effects."
        else
            Ok
                { before with
                    Revision = Revision.successor before.Revision
                    Work = after
                }

    let reminder (request: ReminderRequestRecord) (before: Reminder option) =
        let parent = ReminderCommand.parent request.Command

        let create (plan: ReminderPlan) =
            Reminder.ofAccepted
                (ReminderId.ofOperationId request.OperationId)
                parent
                Revision.first
                plan.Action
                plan.Due
                (ReminderState.after ReminderActKind.Create None |> Option.get)

        let replace old state action due =
            Reminder.ofAccepted
                (Reminder.id old)
                parent
                (Revision.successor (Reminder.revision old))
                action
                due
                state

        let invalid = Error "Invalid retained reminder effects."

        let applyChange id old state change =
            match change with
            | ReminderChange.Update plan when
                Reminder.action old <> plan.Action || Reminder.due old <> plan.Due
                ->
                replace old state plan.Action plan.Due |> Result.map ReminderEffects.Changed
            | ReminderChange.Complete ->
                replace old state (Reminder.action old) (Reminder.due old)
                |> Result.map ReminderEffects.Changed
            | ReminderChange.Cancel ->
                replace old state (Reminder.action old) (Reminder.due old)
                |> Result.map ReminderEffects.Changed
            | ReminderChange.Reopen due ->
                replace old state (Reminder.action old) due
                |> Result.map ReminderEffects.Changed
            | ReminderChange.CompleteAndPlanNext plan when
                ReminderId.ofOperationId request.OperationId <> id
                ->
                match replace old state (Reminder.action old) (Reminder.due old), create plan with
                | Ok completed, Ok next -> Ok(ReminderEffects.CompletedAndPlanned(completed, next))
                | _ -> invalid
            | _ -> invalid

        let after =
            ReminderState.after
                (ReminderCommand.kind request.Command)
                (before |> Option.map Reminder.state)

        match request.Command, before, after with
        | ReminderCommand.Create(_, plan), None, Some _ ->
            create plan |> Result.map ReminderEffects.Created
        | ReminderCommand.Change(_, id, expected, change), Some old, Some state when
            Reminder.caseId old = parent
            && Reminder.id old = id
            && Reminder.revision old = expected
            && Revision.value expected < Revision.Maximum
            ->
            applyChange id old state change
        | _ -> invalid

    let boundedReminderRequest (request: ReminderRequestRecord) effects =
        let parent = ReminderCommand.parent request.Command
        let id = ReminderCommand.target request.OperationId request.Command
        let revision = ReminderCommand.resultingRevision request.Command

        let primary value =
            Reminder.caseId value = parent
            && Reminder.id value = id
            && Some(Reminder.revision value) = revision

        let planMatches (plan: ReminderPlan) value =
            Reminder.action value = plan.Action && Reminder.due value = plan.Due

        let stateMatches act value =
            ReminderState.isResultOf act (Reminder.state value)

        match request.Command, effects with
        | ReminderCommand.Create(_, plan), ReminderEffects.Created value ->
            primary value
            && planMatches plan value
            && stateMatches (ReminderCommand.kind request.Command) value
        | ReminderCommand.Change(_, _, _, ReminderChange.Update plan), ReminderEffects.Changed value ->
            primary value
            && planMatches plan value
            && stateMatches (ReminderCommand.kind request.Command) value
        | ReminderCommand.Change(_, _, _, ReminderChange.Complete), ReminderEffects.Changed value ->
            primary value && stateMatches ReminderActKind.Complete value
        | ReminderCommand.Change(_, _, _, ReminderChange.Cancel), ReminderEffects.Changed value ->
            primary value && stateMatches ReminderActKind.Cancel value
        | ReminderCommand.Change(_, _, _, ReminderChange.Reopen due), ReminderEffects.Changed value ->
            primary value
            && stateMatches (ReminderCommand.kind request.Command) value
            && Reminder.due value = due
        | ReminderCommand.Change(_, _, _, ReminderChange.CompleteAndPlanNext plan),
          ReminderEffects.CompletedAndPlanned(completed, next) ->
            primary completed
            && stateMatches ReminderActKind.CompleteAndPlanNext completed
            && Reminder.id next = ReminderId.ofOperationId request.OperationId
            && Reminder.id next <> Reminder.id completed
            && Reminder.caseId next = parent
            && Reminder.revision next = Revision.first
            && ReminderState.after ReminderActKind.Create None = Some(Reminder.state next)
            && planMatches plan next
        | _ -> false
