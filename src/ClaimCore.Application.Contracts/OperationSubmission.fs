namespace ClaimCore.Application.Contracts

open System
open ClaimCore.Domain

[<RequireQualifiedAccess>]
type SubmissionValue =
    | Text of string
    | Integer of int64
    | Boolean of bool
    | Clear
    | Rows of Map<TaskInputId, SubmissionValue> list

[<RequireQualifiedAccess>]
type OperationBinding<'revision> =
    | Case of target: CaseSelector * expectedRevision: 'revision * paymentId: Guid option
    | Work of target: CaseSelector * expectedRevision: 'revision
    | Note of target: CaseSelector * noteId: Guid option
    | Reminder of target: CaseSelector * current: (Guid * 'revision) option
    | People of actorId: Guid option

type SubmissionEnvelope<'revision, 'operation> =
    {
        OperationId: 'operation
        Task: ActKind
        Binding: OperationBinding<'revision>
        Inputs: Map<TaskInputId, SubmissionValue>
    }

/// Principal resolution is immutable identity selection, performed in the action's transaction.
type ActorPeopleSubmission =
    {
        OperationId: Guid
        ActorId: Guid
        Act: AuthoredPeopleAct
    }

[<RequireQualifiedAccess>]
type SubmittedOperation =
    | Command of OperationInput
    | ActorPeople of ActorPeopleSubmission

[<RequireQualifiedAccess>]
type SubmissionProblem =
    | Shape
    | Rejected of Refusal

/// Shape admission preserves the already assembled typed operation without reparsing it in the service.
type OperationSubmission =
    private
        {
            EnvelopeValue: SubmissionEnvelope<int64, Guid>
            OperationValue: Result<SubmittedOperation, Refusal>
        }

    member this.OperationId = this.EnvelopeValue.OperationId
    member this.Task = this.EnvelopeValue.Task
    member this.Binding = this.EnvelopeValue.Binding
    member this.Inputs = this.EnvelopeValue.Inputs
    member this.Envelope = this.EnvelopeValue
    member this.Operation = this.OperationValue

module OperationSubmission =
    let private target input = TaskInputId.Target input
    let private field input = target (InputTarget.Field input)

    let private issue input violation =
        let target =
            match input with
            | TaskInputId.Target value -> value
            | TaskInputId.Scope -> InputTarget.Scope
            | TaskInputId.CaseId -> InputTarget.CaseId
            | _ -> InputTarget.Text

        SubmissionProblem.Rejected(Refusal.invalidInput (target, violation))

    let private text values input =
        match Map.tryFind input values with
        | Some(SubmissionValue.Text value) -> Ok value
        | Some SubmissionValue.Clear
        | None -> Error(issue input Violation.TextRequired)
        | _ -> Error SubmissionProblem.Shape

    let private optional values input =
        match Map.tryFind input values with
        | None
        | Some SubmissionValue.Clear -> Ok None
        | Some(SubmissionValue.Text value) -> Ok(Some value)
        | _ -> Error SubmissionProblem.Shape

    let private structure result =
        result |> Result.mapError (fun _ -> SubmissionProblem.Shape)

    let private bind next value = Result.bind next value

    let private map2 create left right =
        left |> bind (fun left -> right |> Result.map (create left))

    let private rowProblem ordinal =
        function
        | SubmissionProblem.Rejected(Refusal.InvalidInput issues) ->
            let annotated =
                InputIssues.toList issues
                |> List.map (fun issue ->
                    { issue with
                        GrantRow = GrantOrdinal.create ordinal
                    })

            SubmissionProblem.Rejected(
                Refusal.InvalidInput(InputIssues.ofList annotated |> Option.get)
            )
        | error -> error

    let private collect results =
        let errors =
            results
            |> List.choose (function
                | Error error -> Some error
                | Ok() -> None)

        if errors |> List.contains SubmissionProblem.Shape then
            Error SubmissionProblem.Shape
        else
            let issues =
                errors
                |> List.collect (function
                    | SubmissionProblem.Rejected(Refusal.InvalidInput issues) ->
                        InputIssues.toList issues
                    | _ -> [])

            match InputIssues.ofList issues with
            | Some issues -> Error(SubmissionProblem.Rejected(Refusal.InvalidInput issues))
            | None -> errors |> List.tryHead |> Option.map Error |> Option.defaultValue (Ok())

    let rec private validate (declaration: TaskDeclaration) values =
        let allowed =
            declaration.Inputs
            |> List.filter (fun input -> input.Source = InputSource.Authored)

        let collections =
            declaration.Collections
            |> List.map (fun collection -> TaskCollectionId.input collection.Collection, collection)

        let keys =
            Set.ofList ((allowed |> List.map _.Input) @ (collections |> List.map fst))

        if values |> Map.exists (fun input _ -> not (Set.contains input keys)) then
            Error SubmissionProblem.Shape
        else
            let scalars =
                allowed
                |> List.map (fun input ->
                    match Map.tryFind input.Input values with
                    | None when input.Mode = TaskInputMode.Required ->
                        Error(issue input.Input Violation.TextRequired)
                    | Some SubmissionValue.Clear when
                        not input.Clearable || input.Mode = TaskInputMode.Required
                        ->
                        Error(issue input.Input Violation.TextRequired)
                    | Some(SubmissionValue.Rows _) -> Error SubmissionProblem.Shape
                    | Some(SubmissionValue.Text _) when input.Atom <> TaskAtom.Text ->
                        Error SubmissionProblem.Shape
                    | Some(SubmissionValue.Integer _) when input.Atom <> TaskAtom.Integer ->
                        Error SubmissionProblem.Shape
                    | Some(SubmissionValue.Boolean _) when input.Atom <> TaskAtom.Boolean ->
                        Error SubmissionProblem.Shape
                    | _ -> Ok())

            let rows =
                collections
                |> List.map (fun (input, collection) ->
                    match Map.tryFind input values with
                    | Some(SubmissionValue.Rows rows) ->
                        if
                            rows.Length < TaskCollectionId.minimumRows collection.Collection
                            || rows.Length > TaskCollectionId.maximumRows collection.Collection
                        then
                            Error(
                                issue
                                    input
                                    (Violation.GrantCountRange PeopleAdmission.MaximumGrants)
                            )
                        else
                            rows
                            |> List.mapi (fun ordinal row ->
                                validateRow collection row |> Result.mapError (rowProblem ordinal))
                            |> collect
                    | _ -> Error(issue input Violation.TextRequired))

            collect (scalars @ rows)

    and private validateRow (collection: TaskCollectionDeclaration) row =
        let declaration =
            {
                Task = TaskId.Operation(ActKind.People PeopleActKind.Admit)
                Inputs = collection.Inputs
                Collections = []
            }

        validate declaration row

    let private group values group =
        CaseFields.ofGroup group
        |> List.choose (fun name ->
            match Map.tryFind (field name) values with
            | Some(SubmissionValue.Text value) -> Some(name, value)
            | _ -> None)

    let private updates values group =
        CaseFields.ofGroup group
        |> List.choose (fun name ->
            match Map.tryFind (field name) values with
            | None -> None
            | Some SubmissionValue.Clear -> Some(name, None)
            | Some(SubmissionValue.Text value) -> Some(name, Some value)
            | _ -> None)

    let private principal values =
        let kind =
            text values (target InputTarget.PrincipalKind)
            |> bind (fun value ->
                Vocabulary.tryParse PrincipalKind.vocabulary value
                |> Option.map Ok
                |> Option.defaultValue (Error SubmissionProblem.Shape))

        map2
            (fun kind (issuer, subject) ->
                {
                    PrincipalInput.Kind = kind
                    Issuer = issuer
                    Subject = subject
                })
            kind
            (map2
                (fun issuer subject -> issuer, subject)
                (text values (target InputTarget.PrincipalIssuer))
                (text values (target InputTarget.PrincipalSubject)))

    let private profile values =
        map2
            (fun name contact ->
                {
                    ActorProfileInput.DisplayName = name
                    Contact = contact
                })
            (text values (target InputTarget.ProfileDisplayName))
            (optional values (target InputTarget.ProfileContact))

    let private grant values =
        let role =
            text values (target InputTarget.Role)
            |> bind (fun value ->
                Role.all
                |> List.tryFind (fun role -> Role.token role = value)
                |> Option.map Ok
                |> Option.defaultValue (Error SubmissionProblem.Shape))

        let scope =
            text values TaskInputId.Scope
            |> bind (function
                | "INSTALLATION" ->
                    if
                        Map.containsKey (field BusinessField.CaseReference) values
                        || Map.containsKey TaskInputId.CaseId values
                    then
                        Error SubmissionProblem.Shape
                    else
                        Ok GrantTarget.Installation
                | "CASE" ->
                    match
                        Map.tryFind (field BusinessField.CaseReference) values,
                        Map.tryFind TaskInputId.CaseId values
                    with
                    | Some(SubmissionValue.Text reference), None ->
                        Ok(GrantTarget.CaseReference reference)
                    | None, Some(SubmissionValue.Text id) ->
                        match Guid.TryParseExact(id, "D") with
                        | true, id ->
                            CaseId.create id
                            |> Option.map (GrantTarget.CaseId >> Ok)
                            |> Option.defaultValue (Error SubmissionProblem.Shape)
                        | _ -> Error SubmissionProblem.Shape
                    | _ -> Error SubmissionProblem.Shape
                | _ -> Error SubmissionProblem.Shape)

        map2
            (fun role scope ->
                {
                    AuthoredGrant.Role = role
                    Scope = scope
                })
            role
            scope

    let private workValue values input =
        match Map.tryFind input values with
        | None -> Ok WorkValue.Keep
        | Some SubmissionValue.Clear -> Ok WorkValue.Clear
        | Some(SubmissionValue.Text value) -> Ok(WorkValue.Set value)
        | _ -> Error SubmissionProblem.Shape

    let private caseAct values kind : Result<AuthoredCaseAct, SubmissionProblem> =
        match kind with
        | CaseActKind.Open ->
            group values RecordGroup.Registration
            |> RegistrationInput.create
            |> structure
            |> Result.map CaseAct.Open
        | CaseActKind.AmendRegistration ->
            updates values RecordGroup.Registration
            |> RegistrationUpdateInput.create
            |> structure
            |> Result.map CaseAct.AmendRegistration
        | CaseActKind.Decide ->
            group values RecordGroup.Decision
            |> DecisionInput.create
            |> structure
            |> Result.map CaseAct.Decide
        | CaseActKind.RecordPayment ->
            group values RecordGroup.Payment
            |> PaymentInput.create
            |> structure
            |> Result.map CaseAct.RecordPayment
        | CaseActKind.Void -> text values (target InputTarget.Reason) |> Result.map CaseAct.Void
        | CaseActKind.Reinstate ->
            text values (target InputTarget.Reason) |> Result.map CaseAct.Reinstate
        | CaseActKind.Correct ->
            let registration =
                text values (target (InputTarget.Group RecordGroup.Registration))
                |> bind (function
                    | "KEEP" when (updates values RecordGroup.Registration).IsEmpty ->
                        Ok Amendment.Keep
                    | "UPDATE" ->
                        updates values RecordGroup.Registration
                        |> RegistrationUpdateInput.create
                        |> structure
                        |> Result.map Amendment.Update
                    | _ -> Error SubmissionProblem.Shape)

            let decision =
                text values (target (InputTarget.Group RecordGroup.Decision))
                |> bind (function
                    | "KEEP" when (updates values RecordGroup.Decision).IsEmpty -> Ok Change.Keep
                    | "CLEAR" when (updates values RecordGroup.Decision).IsEmpty -> Ok Change.Clear
                    | "UPDATE" ->
                        updates values RecordGroup.Decision
                        |> DecisionUpdateInput.create
                        |> structure
                        |> Result.map Change.Update
                    | _ -> Error SubmissionProblem.Shape)

            map2
                (fun registration decision ->
                    CaseAct.Correct
                        {
                            Registration = registration
                            Decision = decision
                        })
                registration
                decision
        | CaseActKind.RemovePayment -> Error SubmissionProblem.Shape
        | _ ->
            match (CaseActKind.describe kind).Payload with
            | ActPayload.Bare act -> Ok(CaseAct.Bare act)
            | _ -> Error SubmissionProblem.Shape

    let private peopleAct values kind : Result<AuthoredPeopleAct, SubmissionProblem> =
        match kind with
        | PeopleActKind.Register -> profile values |> Result.map PeopleAct.Register
        | PeopleActKind.Admit ->
            let grants =
                match Map.tryFind (target InputTarget.Grants) values with
                | Some(SubmissionValue.Rows rows) ->
                    rows
                    |> List.mapi (fun ordinal values -> ordinal, values)
                    |> List.fold
                        (fun result (ordinal, values) ->
                            map2
                                (fun grants grant -> grants @ [ grant ])
                                result
                                (grant values |> Result.mapError (rowProblem ordinal)))
                        (Ok [])
                | _ -> Error SubmissionProblem.Shape

            map2
                (fun profile grants -> PeopleAct.Admit { Profile = profile; Grants = grants })
                (profile values)
                grants
        | PeopleActKind.SetProfile -> profile values |> Result.map PeopleAct.SetProfile
        | PeopleActKind.Grant -> grant values |> Result.map PeopleAct.Grant
        | PeopleActKind.Revoke -> grant values |> Result.map PeopleAct.Revoke
        | PeopleActKind.Enable -> Ok PeopleAct.Enable
        | PeopleActKind.Disable -> Ok PeopleAct.Disable
        | PeopleActKind.EraseProfile -> Ok PeopleAct.EraseProfile
        | PeopleActKind.ProvisionOwner -> Error SubmissionProblem.Shape

    let private reminderAct values kind current =
        let plan =
            map2
                (fun action due ->
                    {
                        ReminderPlanInput.Action = action
                        Due = due
                    })
                (text values (target InputTarget.ReminderAction))
                (text values (target InputTarget.ReminderDueDate))

        let act =
            match kind, current with
            | ReminderActKind.Create, None -> plan |> Result.map ReminderAct.Create
            | ReminderActKind.Update, Some(id, revision) ->
                plan |> Result.map (fun plan -> ReminderAct.Update(id, revision, plan))
            | ReminderActKind.CompleteAndPlanNext, Some(id, revision) ->
                plan
                |> Result.map (fun plan -> ReminderAct.CompleteAndPlanNext(id, revision, plan))
            | ReminderActKind.Reopen, Some(id, revision) ->
                text values (target InputTarget.ReminderDueDate)
                |> Result.map (fun due -> ReminderAct.Reopen(id, revision, due))
            | ReminderActKind.Complete, Some(id, revision) -> Ok(ReminderAct.Complete(id, revision))
            | ReminderActKind.Cancel, Some(id, revision) -> Ok(ReminderAct.Cancel(id, revision))
            | _ -> Error SubmissionProblem.Shape

        act

    let private assembleDeclared (submission: SubmissionEnvelope<int64, Guid>) =
        let declaration = TaskDeclarations.describe (TaskId.Operation submission.Task)

        validate declaration submission.Inputs
        |> bind (fun () ->
            let values = submission.Inputs
            let command input = Ok(SubmittedOperation.Command input)

            match submission.Task, submission.Binding with
            | ActKind.Case kind, OperationBinding.Case(selection, expected, payment) ->
                let act =
                    match kind, payment with
                    | CaseActKind.RemovePayment, Some id -> Ok(CaseAct.RemovePayment id)
                    | CaseActKind.RemovePayment, None
                    | _, Some _ -> Error SubmissionProblem.Shape
                    | _ -> caseAct values kind

                act
                |> bind (fun act ->
                    command (
                        OperationInput.Case
                            {
                                OperationId = submission.OperationId
                                Target = selection
                                ExpectedRevision = expected
                                Act = act
                            }
                    ))
            | ActKind.Work WorkActKind.UpdateWork, OperationBinding.Work(selection, expected) ->
                map2
                    (fun responsible summary ->
                        WorkAct.UpdateWork(
                            {
                                Responsible = responsible
                                Summary = summary
                            }
                            : CaseWorkInput
                        ))
                    (workValue values (field BusinessField.ResponsibleActor))
                    (workValue values (field BusinessField.HandlingSummary))
                |> bind (fun act ->
                    command (
                        OperationInput.Work
                            {
                                OperationId = submission.OperationId
                                Target = selection
                                ExpectedRevision = expected
                                Act = act
                            }
                    ))
            | ActKind.Note NoteActKind.AddNote, OperationBinding.Note(selection, None) ->
                text values (target InputTarget.NoteText)
                |> bind (fun text ->
                    command (
                        OperationInput.Note
                            {
                                OperationId = submission.OperationId
                                Target = selection
                                Act = AuthoredNoteAct.AddNote text
                            }
                    ))
            | ActKind.Note NoteActKind.RemoveNote, OperationBinding.Note(selection, Some id) ->
                command (
                    OperationInput.Note
                        {
                            OperationId = submission.OperationId
                            Target = selection
                            Act = AuthoredNoteAct.RemoveNote id
                        }
                )
            | ActKind.Reminder kind, OperationBinding.Reminder(selection, current) ->
                let act = reminderAct values kind current

                act
                |> bind (fun act ->
                    command (
                        OperationInput.Reminder
                            {
                                OperationId = submission.OperationId
                                Target = selection
                                Act = act
                            }
                    ))
            | ActKind.People kind, OperationBinding.People actorId ->
                peopleAct values kind
                |> bind (fun act ->
                    match kind, actorId with
                    | (PeopleActKind.Register | PeopleActKind.Admit), None ->
                        principal values
                        |> bind (fun target ->
                            command (
                                OperationInput.People
                                    {
                                        OperationId = submission.OperationId
                                        Target = target
                                        Act = act
                                    }
                            ))
                    | (PeopleActKind.Register | PeopleActKind.Admit), Some _
                    | _, None -> Error SubmissionProblem.Shape
                    | _, Some actorId ->
                        Ok(
                            SubmittedOperation.ActorPeople
                                {
                                    OperationId = submission.OperationId
                                    ActorId = actorId
                                    Act = act
                                }
                        ))
            | _ -> Error SubmissionProblem.Shape)

    let assemble submission =
        if submission.Task = ActKind.People PeopleActKind.ProvisionOwner then
            Error SubmissionProblem.Shape
        else
            assembleDeclared submission

    let admit (envelope: SubmissionEnvelope<int64, Guid>) =
        match assemble envelope with
        | Error SubmissionProblem.Shape -> Error()
        | Error(SubmissionProblem.Rejected refusal) ->
            Ok
                {
                    EnvelopeValue = envelope
                    OperationValue = Error refusal
                }
        | Ok operation ->
            Ok
                {
                    EnvelopeValue = envelope
                    OperationValue = Ok operation
                }

    let syntaxDraft (draft: SubmissionEnvelope<int64 option, Guid option>) =
        let reference =
            function
            | CaseSelector.Reference value -> Some value
            | CaseSelector.Id _ -> None

        let syntax intent = Ok(Validation.syntaxIntent intent)

        if draft.Task = ActKind.People PeopleActKind.ProvisionOwner then
            Error SubmissionProblem.Shape
        else
            validate (TaskDeclarations.describe (TaskId.Operation draft.Task)) draft.Inputs
            |> bind (fun () ->
                let values = draft.Inputs

                match draft.Task, draft.Binding with
                | ActKind.Case kind, OperationBinding.Case(selection, expected, payment) ->
                    let act =
                        match kind, payment with
                        | CaseActKind.RemovePayment, Some id -> Ok(CaseAct.RemovePayment id)
                        | CaseActKind.RemovePayment, None
                        | _, Some _ -> Error SubmissionProblem.Shape
                        | _ -> caseAct values kind

                    act
                    |> bind (fun act ->
                        syntax (
                            AuthoredIntent.Case(
                                draft.OperationId,
                                reference selection,
                                expected,
                                act
                            )
                        ))
                | ActKind.Work _, OperationBinding.Work(selection, expected) ->
                    map2
                        (fun responsible summary ->
                            {
                                CaseWorkInput.Responsible = responsible
                                Summary = summary
                            })
                        (workValue values (field BusinessField.ResponsibleActor))
                        (workValue values (field BusinessField.HandlingSummary))
                    |> bind (fun work ->
                        syntax (
                            AuthoredIntent.Work(
                                draft.OperationId,
                                reference selection,
                                expected,
                                work
                            )
                        ))
                | ActKind.Note kind, OperationBinding.Note(selection, selected) ->
                    let act =
                        match kind, selected with
                        | NoteActKind.AddNote, None ->
                            text values (target InputTarget.NoteText)
                            |> Result.map AuthoredNoteAct.AddNote
                        | NoteActKind.RemoveNote, Some id -> Ok(AuthoredNoteAct.RemoveNote id)
                        | _ -> Error SubmissionProblem.Shape

                    act
                    |> bind (fun act ->
                        syntax (AuthoredIntent.Note(draft.OperationId, reference selection, act)))
                | ActKind.Reminder kind, OperationBinding.Reminder(selection, current) ->
                    reminderAct values kind current
                    |> bind (fun act ->
                        syntax (
                            AuthoredIntent.Reminder(draft.OperationId, reference selection, act)
                        ))
                | ActKind.People kind, OperationBinding.People actor ->
                    peopleAct values kind
                    |> bind (fun act ->
                        match kind, actor with
                        | (PeopleActKind.Register | PeopleActKind.Admit), None ->
                            principal values
                            |> bind (fun principal ->
                                syntax (AuthoredIntent.People(draft.OperationId, principal, act)))
                        | (PeopleActKind.Register | PeopleActKind.Admit), Some _
                        | _, None -> Error SubmissionProblem.Shape
                        | _, Some actor ->
                            if ActorId.create actor |> Option.isNone then
                                Error SubmissionProblem.Shape
                            else
                                let result = People.syntaxAction draft.OperationId act

                                match result with
                                | Error(Refusal.InvalidInput issues) -> Ok(Some issues)
                                | _ -> Ok None)
                | _ -> Error SubmissionProblem.Shape)

    let completeDraft (draft: SubmissionEnvelope<int64 option, Guid option>) =
        let binding =
            match draft.Binding with
            | OperationBinding.Case(target, Some revision, payment) ->
                Some(OperationBinding.Case(target, revision, payment))
            | OperationBinding.Work(target, Some revision) ->
                Some(OperationBinding.Work(target, revision))
            | OperationBinding.Note(target, note) -> Some(OperationBinding.Note(target, note))
            | OperationBinding.Reminder(target, None) ->
                Some(OperationBinding.Reminder(target, None))
            | OperationBinding.Reminder(target, Some(id, Some revision)) ->
                Some(OperationBinding.Reminder(target, Some(id, revision)))
            | OperationBinding.People actor -> Some(OperationBinding.People actor)
            | _ -> None

        match draft.OperationId, binding with
        | Some id, Some binding ->
            admit
                {
                    OperationId = id
                    Task = draft.Task
                    Binding = binding
                    Inputs = draft.Inputs
                }
            |> Result.toOption
        | _ -> None

    let syntax (submission: OperationSubmission) =
        match submission.Operation with
        | Error(Refusal.InvalidInput issues) -> Some issues
        | Ok(SubmittedOperation.Command command) -> OperationInput.syntax command
        | Ok(SubmittedOperation.ActorPeople input) ->
            match People.syntaxAction (Some input.OperationId) input.Act with
            | Error(Refusal.InvalidInput issues) -> Some issues
            | _ -> None
        | _ -> None
