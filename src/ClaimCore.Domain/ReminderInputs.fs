namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type ReminderInput =
    | Create of ReminderPlan
    | Change of ReminderId * Revision * ReminderChange


module ReminderInput =
    let private input target parse raw =
        parse raw
        |> Result.mapError (fun violation -> Refusal.invalidInput (target, violation))

    let private revision target minimum raw =
        Revision.create raw
        |> Option.filter (fun value -> value >= minimum)
        |> Option.map Ok
        |> Option.defaultValue (
            Error(Refusal.invalidInput (target, Violation.ExpectedRevisionRange))
        )

    let private reminderId raw =
        ReminderId.create raw
        |> Option.map Ok
        |> Option.defaultValue (
            Error(Refusal.invalidInput (InputTarget.ReminderId, Violation.ReminderIdRequired))
        )

    let private plan admitAction (raw: ReminderPlanInput) =
        Admission.map2
            (fun action due ->
                {
                    ReminderPlan.Action = action
                    Due = due
                })
            (input
                InputTarget.ReminderAction
                (ReminderAction.parse >> Result.bind admitAction)
                raw.Action)
            (input InputTarget.ReminderDueDate CalendarDate.parse raw.Due)

    let private parseWith admitAction =
        function
        | AuthoredReminderAct.Create value ->
            plan admitAction value |> Result.map ReminderInput.Create
        | act ->
            let id, expected, change =
                match act with
                | AuthoredReminderAct.Update(id, expected, value) ->
                    id, expected, plan admitAction value |> Result.map ReminderChange.Update
                | AuthoredReminderAct.Complete(id, expected) ->
                    id, expected, Ok ReminderChange.Complete
                | AuthoredReminderAct.Cancel(id, expected) -> id, expected, Ok ReminderChange.Cancel
                | AuthoredReminderAct.Reopen(id, expected, date) ->
                    id,
                    expected,
                    input InputTarget.ReminderDueDate CalendarDate.parse date
                    |> Result.map ReminderChange.Reopen
                | AuthoredReminderAct.CompleteAndPlanNext(id, expected, value) ->
                    id,
                    expected,
                    plan admitAction value |> Result.map ReminderChange.CompleteAndPlanNext
                | AuthoredReminderAct.Create _ -> invalidOp "Creation is handled separately."

            Admission.map2
                (fun (id, expected) change -> ReminderInput.Change(id, expected, change))
                (Admission.map2
                    (fun id expected -> id, expected)
                    (reminderId id)
                    (revision InputTarget.ExpectedReminderRevision Revision.first expected))
                change

    let parse input = parseWith Ok input
    let admit input = parseWith ReminderAction.admit input

    let resolve parent =
        function
        | ReminderInput.Create plan -> ReminderCommand.Create(parent, plan)
        | ReminderInput.Change(id, expected, change) ->
            ReminderCommand.Change(parent, id, expected, change)

    let ofCommand =
        function
        | ReminderCommand.Create(_, plan) -> ReminderInput.Create plan
        | ReminderCommand.Change(_, id, expected, change) ->
            ReminderInput.Change(id, expected, change)

    let authored input : AuthoredReminderAct =
        let plan (value: ReminderPlan) : ReminderPlanInput =
            {
                Action = ReminderAction.format value.Action
                Due = CalendarDate.format value.Due
            }

        match input with
        | ReminderInput.Create value -> ReminderAct.Create(plan value)
        | ReminderInput.Change(id, expected, change) ->
            let id, expected = ReminderId.value id, Revision.value expected

            match change with
            | ReminderChange.Update value -> ReminderAct.Update(id, expected, plan value)
            | ReminderChange.Complete -> ReminderAct.Complete(id, expected)
            | ReminderChange.Cancel -> ReminderAct.Cancel(id, expected)
            | ReminderChange.Reopen due -> ReminderAct.Reopen(id, expected, CalendarDate.format due)
            | ReminderChange.CompleteAndPlanNext value ->
                ReminderAct.CompleteAndPlanNext(id, expected, plan value)
