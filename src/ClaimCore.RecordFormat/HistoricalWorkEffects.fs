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
                ReminderState.Pending

        let replace old state action due =
            Reminder.ofAccepted
                (Reminder.id old)
                parent
                (Revision.successor (Reminder.revision old))
                action
                due
                state

        let invalid = Error "Invalid retained reminder effects."

        match request.Command, before with
        | ReminderCommand.Create(_, plan), None -> create plan |> Result.map ReminderEffects.Created
        | ReminderCommand.Change(_, id, expected, change), Some old when
            Reminder.caseId old = parent
            && Reminder.id old = id
            && Reminder.revision old = expected
            && Revision.value expected < Revision.Maximum
            ->
            let pending = Reminder.state old = ReminderState.Pending

            match change with
            | ReminderChange.Update plan when
                pending && (Reminder.action old <> plan.Action || Reminder.due old <> plan.Due)
                ->
                replace old ReminderState.Pending plan.Action plan.Due
                |> Result.map ReminderEffects.Changed
            | ReminderChange.Complete when pending ->
                replace old ReminderState.Completed (Reminder.action old) (Reminder.due old)
                |> Result.map ReminderEffects.Changed
            | ReminderChange.Cancel when pending ->
                replace old ReminderState.Cancelled (Reminder.action old) (Reminder.due old)
                |> Result.map ReminderEffects.Changed
            | ReminderChange.Reopen due when not pending ->
                replace old ReminderState.Pending (Reminder.action old) due
                |> Result.map ReminderEffects.Changed
            | ReminderChange.CompleteAndPlanNext plan when
                pending && ReminderId.ofOperationId request.OperationId <> id
                ->
                match
                    replace old ReminderState.Completed (Reminder.action old) (Reminder.due old),
                    create plan
                with
                | Ok completed, Ok next -> Ok(ReminderEffects.CompletedAndPlanned(completed, next))
                | _ -> invalid
            | _ -> invalid
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

        match request.Command, effects with
        | ReminderCommand.Create(_, plan), ReminderEffects.Created value ->
            primary value
            && planMatches plan value
            && Reminder.state value = ReminderState.Pending
        | ReminderCommand.Change(_, _, _, ReminderChange.Update plan), ReminderEffects.Changed value ->
            primary value
            && planMatches plan value
            && Reminder.state value = ReminderState.Pending
        | ReminderCommand.Change(_, _, _, ReminderChange.Complete), ReminderEffects.Changed value ->
            primary value && Reminder.state value = ReminderState.Completed
        | ReminderCommand.Change(_, _, _, ReminderChange.Cancel), ReminderEffects.Changed value ->
            primary value && Reminder.state value = ReminderState.Cancelled
        | ReminderCommand.Change(_, _, _, ReminderChange.Reopen due), ReminderEffects.Changed value ->
            primary value
            && Reminder.state value = ReminderState.Pending
            && Reminder.due value = due
        | ReminderCommand.Change(_, _, _, ReminderChange.CompleteAndPlanNext plan),
          ReminderEffects.CompletedAndPlanned(completed, next) ->
            primary completed
            && Reminder.state completed = ReminderState.Completed
            && Reminder.id next = ReminderId.ofOperationId request.OperationId
            && Reminder.id next <> Reminder.id completed
            && Reminder.caseId next = parent
            && Reminder.revision next = Revision.first
            && Reminder.state next = ReminderState.Pending
            && planMatches plan next
        | _ -> false
