namespace ClaimCore.Domain

open System

/// Authored values available without a service clock or accepted business state.
[<RequireQualifiedAccess>]
type AuthoredInput =
    | Case of
        operationId: Guid *
        reference: string option *
        expectedRevision: int64 *
        AuthoredCaseAct
    | Work of operationId: Guid * expectedRevision: int64 * CaseWorkInput
    | Note of operationId: Guid * AuthoredNoteAct
    | Reminder of operationId: Guid * AuthoredReminderAct
    | Authority of AuthorityRequest

/// A draft can omit metadata that only the current read or execution will supply.
[<RequireQualifiedAccess>]
type AuthoredIntent =
    | Case of
        operationId: Guid option *
        reference: string option *
        expectedRevision: int64 option *
        act: AuthoredCaseAct
    | Work of
        operationId: Guid option *
        reference: string option *
        expectedRevision: int64 option *
        work: CaseWorkInput
    | Reminder of
        operationId: Guid option *
        reference: string option *
        act: ReminderAct<int64 option>
    | Note of operationId: Guid option * reference: string option * act: AuthoredNoteAct
    | Authority of operationId: Guid option * target: PrincipalInput * act: AuthoredAuthorityAct

module Validation =
    let private at target parse raw =
        parse raw
        |> Result.mapError (fun violation -> Refusal.invalidInput (target, violation))

    let private operation id =
        if OperationId.create id |> Option.isSome then
            Ok()
        else
            Error(Refusal.invalidInput (InputTarget.OperationId, Violation.OperationIdRequired))

    let private revision target value =
        if Revision.create value |> Option.isSome then
            Ok()
        else
            Error(Refusal.invalidInput (target, Violation.ExpectedRevisionRange))

    let private reminderId id =
        if ReminderId.create id |> Option.isSome then
            Ok()
        else
            Error(Refusal.invalidInput (InputTarget.ReminderId, Violation.ReminderIdRequired))

    let private plan (input: ReminderPlanInput) =
        Admission.collect
            [
                at
                    InputTarget.ReminderAction
                    (ReminderAction.parse >> Result.bind ReminderAction.admit)
                    input.Action
                |> Result.map ignore
                at InputTarget.ReminderDueDate CalendarDate.parse input.Due |> Result.map ignore
            ]

    let private reminderWith revisionCheck =
        function
        | ReminderAct.Create input -> plan input
        | act ->
            let id, expected, payload =
                match act with
                | ReminderAct.Update(id, expected, input)
                | ReminderAct.CompleteAndPlanNext(id, expected, input) -> id, expected, plan input
                | ReminderAct.Complete(id, expected)
                | ReminderAct.Cancel(id, expected) -> id, expected, Ok()
                | ReminderAct.Reopen(id, expected, due) ->
                    id,
                    expected,
                    at InputTarget.ReminderDueDate CalendarDate.parse due |> Result.map ignore
                | ReminderAct.Create _ -> invalidOp "Creation has no prior reminder revision."

            Admission.collect [ reminderId id; revisionCheck expected; payload ]

    /// Validate the authored draft before any read, without fabricating an ID, revision or clock.
    let syntaxIntent input =
        let supplied check =
            Option.map check >> Option.defaultValue (Ok())

        let reference =
            at
                (InputTarget.Field BusinessField.CaseReference)
                (CaseReference.parse >> Result.map ignore)

        let result =
            match input with
            | AuthoredIntent.Case(id, reference, expected, act) ->
                RequestValidation.syntaxIntent id reference expected act
            | AuthoredIntent.Work(id, target, expected, work) ->
                Admission.collect
                    [
                        supplied operation id
                        supplied reference target
                        supplied (revision InputTarget.ExpectedWorkRevision) expected
                        CaseWorkInput.admit work |> InputValidation.group |> Result.map ignore
                    ]
            | AuthoredIntent.Reminder(id, target, act) ->
                Admission.collect
                    [
                        supplied operation id
                        supplied reference target
                        reminderWith (supplied (revision InputTarget.ExpectedReminderRevision)) act
                    ]
            | AuthoredIntent.Note(id, target, act) ->
                Admission.collect
                    [
                        supplied operation id
                        supplied reference target
                        NoteInput.admit act |> Result.map ignore
                    ]
            | AuthoredIntent.Authority(id, target, act) -> Authority.syntaxIntent id target act

        match InputValidation.ordered result with
        | Error(Refusal.InvalidInput issues) -> Some issues
        | _ -> None

    /// Complete requests use the same authored syntax owner with their real supplied metadata.
    let syntax input =
        let reminder =
            function
            | ReminderAct.Create plan -> ReminderAct.Create plan
            | ReminderAct.Update(id, expected, plan) -> ReminderAct.Update(id, Some expected, plan)
            | ReminderAct.Complete(id, expected) -> ReminderAct.Complete(id, Some expected)
            | ReminderAct.Cancel(id, expected) -> ReminderAct.Cancel(id, Some expected)
            | ReminderAct.Reopen(id, expected, due) -> ReminderAct.Reopen(id, Some expected, due)
            | ReminderAct.CompleteAndPlanNext(id, expected, plan) ->
                ReminderAct.CompleteAndPlanNext(id, Some expected, plan)

        match input with
        | AuthoredInput.Case(id, reference, expected, act) ->
            AuthoredIntent.Case(Some id, reference, Some expected, act)
        | AuthoredInput.Work(id, expected, work) ->
            AuthoredIntent.Work(Some id, None, Some expected, work)
        | AuthoredInput.Reminder(id, act) -> AuthoredIntent.Reminder(Some id, None, reminder act)
        | AuthoredInput.Note(id, act) -> AuthoredIntent.Note(Some id, None, act)
        | AuthoredInput.Authority request ->
            AuthoredIntent.Authority(Some request.OperationId, request.Target, request.Act)
        |> syntaxIntent
