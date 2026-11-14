namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Concise event meaning and highlights come from the same scoped, validated historical effects as full changes.
module internal TimelineOverview =
    let private fault = Error CoreFault.StoreIntegrityError

    let private overview event highlights : Result<ClaimCore.Domain.TimelineOverview, CoreFault> =
        Ok
            {
                Event = event
                Highlights = highlights
            }

    let private select predicate changes =
        changes
        |> List.indexed
        |> List.choose (fun (index, change) ->
            if predicate change then
                Some(TimelineHighlight.Change index)
            else
                None)

    let private payableChange =
        function
        | TimelineChange.CaseMoney value -> value.FieldName = BusinessField.PayableAmount
        | _ -> false

    let private paymentChange =
        function
        | TimelineChange.Payments _ -> true
        | _ -> false

    let private caseHighlights act changes =
        match act with
        | CaseActKind.AmendRegistration
        | CaseActKind.Correct -> select (fun _ -> true) changes
        | CaseActKind.Decide
        | CaseActKind.WithdrawDecision -> select payableChange changes
        | CaseActKind.RecordPayment
        | CaseActKind.RemovePayment -> select paymentChange changes
        | CaseActKind.Open
        | CaseActKind.Close
        | CaseActKind.Reopen
        | CaseActKind.Void
        | CaseActKind.Reinstate
        | CaseActKind.Erase -> []

    let private work changes =
        let responsible =
            changes
            |> List.tryPick (function
                | TimelineChange.Responsibility value -> Some value
                | _ -> None)

        let summary =
            changes
            |> List.tryPick (function
                | TimelineChange.HandlingSummary value -> Some value
                | _ -> None)

        let highlights =
            changes
            |> select (function
                | TimelineChange.HandlingSummary _ -> true
                | _ -> false)

        match responsible, summary with
        | Some { After = Some person }, _ ->
            overview (TimelineEvent.ResponsibilityAssigned person) highlights
        | Some { Before = Some person; After = None }, _ ->
            overview (TimelineEvent.ResponsibilityCleared person) highlights
        | Some _, _ -> fault
        | None, Some { Before = None; After = Some _ } ->
            overview TimelineEvent.HandlingSummaryAdded highlights
        | None, Some { Before = Some _; After = None } ->
            overview TimelineEvent.HandlingSummaryCleared highlights
        | None, Some { Before = Some _; After = Some _ } ->
            overview TimelineEvent.HandlingSummaryChanged highlights
        | None, Some _ -> fault
        | None, None -> overview TimelineEvent.WorkRecorded []

    let private reminderHighlights dueOnly changes =
        changes
        |> List.indexed
        |> List.choose (fun (index, change) ->
            match change with
            | TimelineChange.Reminder difference ->
                let facets =
                    [
                        if not dueOnly && difference.Action.IsSome then
                            TimelineFacet.Action
                        if difference.Due.IsSome then
                            TimelineFacet.Due
                    ]

                if facets.IsEmpty then
                    None
                else
                    Some(TimelineHighlight.Reminder(index, facets))
            | _ -> None)

    let private reminder scope act effects changes =
        let within value =
            scope |> Option.forall (fun id -> id = Reminder.id value)

        match act, effects with
        | ReminderActKind.Create, ReminderEffects.Created value when within value ->
            overview (TimelineEvent.ReminderPlanned(Reminder.action value, Reminder.due value)) []
        | ReminderActKind.Update, ReminderEffects.Changed value when within value ->
            overview
                (TimelineEvent.ReminderChanged(Reminder.action value))
                (reminderHighlights false changes)
        | ReminderActKind.Complete, ReminderEffects.Changed value when within value ->
            overview (TimelineEvent.ReminderCompleted(Reminder.action value)) []
        | ReminderActKind.Cancel, ReminderEffects.Changed value when within value ->
            overview (TimelineEvent.ReminderCancelled(Reminder.action value)) []
        | ReminderActKind.Reopen, ReminderEffects.Changed value when within value ->
            overview
                (TimelineEvent.ReminderReopened(Reminder.action value, Reminder.due value))
                (reminderHighlights true changes)
        | ReminderActKind.CompleteAndPlanNext, ReminderEffects.CompletedAndPlanned(completed, next) ->
            match scope with
            | None ->
                overview
                    (TimelineEvent.ReminderCompletedAndPlanned(
                        Reminder.action completed,
                        Reminder.action next,
                        Reminder.due next
                    ))
                    []
            | Some id when id = Reminder.id completed ->
                overview (TimelineEvent.ReminderCompleted(Reminder.action completed)) []
            | Some id when id = Reminder.id next ->
                overview (TimelineEvent.ReminderPlanned(Reminder.action next, Reminder.due next)) []
            | Some _ -> fault
        | _ -> fault

    /// The caller applies disclosure and entity-range filtering before entering this constructor.
    let create entity subject content changes =
        match entity, subject, content with
        | (None | Some ChangeEntity.Case),
          TimelineSubject.CaseFact(_, act),
          EntryContent.Case(request, Some _) when CaseAct.kind request.Act = act ->
            overview (TimelineEvent.CaseActRecorded act) (caseHighlights act changes)
        | None, TimelineSubject.Note(act, _), EntryContent.Note _ ->
            overview (TimelineEvent.NoteRecorded act) []
        | (None | Some ChangeEntity.Work), TimelineSubject.Work _, EntryContent.Work _ ->
            work changes
        | (None | Some(ChangeEntity.Reminder _)),
          TimelineSubject.Reminder(act, _, _, _),
          EntryContent.Reminder(request, effects) when ReminderCommand.kind request.Command = act ->
            let scope =
                entity
                |> Option.bind (function
                    | ChangeEntity.Reminder id -> Some id
                    | _ -> None)

            reminder scope act effects changes
        | _ -> fault
