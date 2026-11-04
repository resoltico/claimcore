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

    /// Checks formats, lengths and characters, never future dates or accepted-state rules.
    let syntax input =
        let result =
            match input with
            | AuthoredInput.Case(id, reference, expected, act) ->
                Admission.collect
                    [
                        RequestValidation.syntax id reference expected act
                        StateValidation.authoredChronology None act
                    ]
            | AuthoredInput.Authority request -> Authority.parse request |> Result.map ignore
            | AuthoredInput.Work(id, expected, input) ->
                Admission.collect
                    [
                        operation id
                        revision InputTarget.ExpectedWorkRevision expected
                        CaseWorkInput.admit input |> InputValidation.group |> Result.map ignore
                    ]
            | AuthoredInput.Reminder(id, input) ->
                Admission.collect
                    [
                        operation id
                        reminderWith (revision InputTarget.ExpectedReminderRevision) input
                    ]

        match InputValidation.ordered result with
        | Error(Refusal.InvalidInput issues) -> Some issues
        | _ -> None


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
                Admission.collect
                    [
                        RequestValidation.syntaxIntent id reference expected act
                        StateValidation.authoredChronology None act
                    ]
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
            | AuthoredIntent.Authority(id, target, act) -> Authority.syntaxIntent id target act

        match InputValidation.ordered result with
        | Error(Refusal.InvalidInput issues) -> Some issues
        | _ -> None
