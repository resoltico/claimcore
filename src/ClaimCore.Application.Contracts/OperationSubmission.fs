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
    | CaseNew of expectedRevision: 'revision
    | Case of
        target: CaseSelector *
        expectedRevision: 'revision *
        paymentId: Guid option *
        expectedFamilySequence: int64 option
    | Work of target: CaseSelector * expectedRevision: 'revision
    | Note of target: CaseSelector * noteId: Guid option
    | Reminder of target: CaseSelector * current: (Guid * 'revision) option
    | People of actorId: Guid option * expectedRevision: 'revision

type SubmissionEnvelope<'revision, 'operation> =
    {
        OperationId: 'operation
        Task: TaskId
        Binding: OperationBinding<'revision>
        Inputs: Map<TaskInputId, SubmissionValue>
    }

type SubmittedOperation = { Binding: Binding; Inputs: InputMap }

[<RequireQualifiedAccess>]
type SubmissionProblem =
    | Shape
    | Rejected of Refusal

/// Authored atoms and reviewed binding survive admission without a per-subject command layer.
type OperationSubmission =
    private
        {
            EnvelopeValue: SubmissionEnvelope<int64, Guid>
            OperationValue: Result<SubmittedOperation, Refusal>
        }

    member this.OperationId = this.EnvelopeValue.OperationId
    member this.Task = this.EnvelopeValue.Task
    member this.Envelope = this.EnvelopeValue
    member this.Operation = this.OperationValue

module OperationSubmission =
    let rawBinding =
        function
        | Binding.CaseNew revision -> OperationBinding.CaseNew(Revision.value revision)
        | Binding.Case(target, revision) ->
            OperationBinding.Case(target, Revision.value revision, None, None)
        | Binding.CasePayment(target, revision, id) ->
            OperationBinding.Case(target, Revision.value revision, Some(OperationId.value id), None)
        | Binding.CaseErase(target, revision, sequence) ->
            OperationBinding.Case(
                target,
                Revision.value revision,
                None,
                Some(Sequence.value sequence)
            )
        | Binding.Work(target, revision) -> OperationBinding.Work(target, Revision.value revision)
        | Binding.ReminderNew target -> OperationBinding.Reminder(target, None)
        | Binding.Reminder(target, id, revision) ->
            OperationBinding.Reminder(target, Some(ReminderId.value id, Revision.value revision))
        | Binding.NoteNew target -> OperationBinding.Note(target, None)
        | Binding.Note(target, id) -> OperationBinding.Note(target, Some(NoteId.value id))
        | Binding.PeopleNew revision -> OperationBinding.People(None, Revision.value revision)
        | Binding.People(id, revision) ->
            OperationBinding.People(Some(ActorId.value id), Revision.value revision)

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

    let rec private validate
        (inputs: TaskInputDeclaration list)
        (declaredCollections: TaskCollectionDeclaration list)
        values
        =
        let allowed =
            inputs |> List.filter (fun input -> input.Source = InputSource.Authored)

        let collections =
            declaredCollections
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
                            rows.Length < collection.MinimumRows
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
        validate collection.Inputs [] row

    let private mapInputs
        (declaredCollections: TaskCollectionDeclaration list)
        (values: Map<TaskInputId, SubmissionValue>)
        =
        let atom =
            function
            | SubmissionValue.Text text -> Some(InputValue.Text text)
            | SubmissionValue.Integer value -> Some(InputValue.Integer value)
            | SubmissionValue.Boolean value -> Some(InputValue.Boolean value)
            | SubmissionValue.Clear -> Some InputValue.Null
            | SubmissionValue.Rows _ -> None

        let scalars =
            values
            |> Map.toList
            |> List.choose (fun (key, value) -> atom value |> Option.map (fun value -> key, value))
            |> Map.ofList

        let collections =
            values
            |> Map.toList
            |> List.choose (fun (key, value) ->
                match value with
                | SubmissionValue.Rows rows ->
                    declaredCollections
                    |> List.map _.Collection
                    |> List.tryFind (fun collection -> TaskCollectionId.input collection = key)
                    |> Option.map (fun collection ->
                        collection,
                        rows
                        |> List.map (
                            Map.toList
                            >> List.choose (fun (key, value) ->
                                atom value |> Option.map (fun value -> key, value))
                            >> Map.ofList
                        ))
                | _ -> None)
            |> Map.ofList

        InputMap.create scalars collections
        |> (function
        | Some value -> Ok value
        | None -> Error SubmissionProblem.Shape)

    let private version minimum input raw =
        Revision.create raw
        |> Option.filter (fun value -> Revision.value value >= minimum)
        |> Option.map Ok
        |> Option.defaultValue (Error(issue (target input) Violation.ExpectedRevisionRange))

    let private creationVersion raw =
        if raw = SubmissionDeclarations.creationRevision then
            Ok Revision.zero
        else
            Error(issue (target InputTarget.ExpectedRevision) Violation.ExpectedRevisionRange)

    let private identity create input violation raw =
        create raw
        |> Option.map Ok
        |> Option.defaultValue (Error(issue (target input) violation))

    let private actorIdentity id =
        ActorId.create id
        |> Option.map Ok
        |> Option.defaultValue (
            Error(SubmissionProblem.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
        )

    let admitBinding (declaration: TaskDeclaration) raw =
        let binds input =
            declaration.Inputs
            |> List.exists (fun item ->
                item.Input = target input && item.Source = InputSource.Binding)

        match declaration.Target, raw with
        | ActSubject.Case, OperationBinding.CaseNew expected when declaration.Creates ->
            creationVersion expected |> Result.map Binding.CaseNew
        | ActSubject.Case, OperationBinding.Case(selection, expected, payment, family) when
            not declaration.Creates
            ->
            version SubmissionDeclarations.existingRevision InputTarget.ExpectedRevision expected
            |> bind (fun expected ->
                if binds InputTarget.PaymentId then
                    match payment, family with
                    | Some id, None ->
                        identity
                            OperationId.create
                            InputTarget.PaymentId
                            Violation.PaymentIdRequired
                            id
                        |> Result.map (fun id -> Binding.CasePayment(selection, expected, id))
                    | _ -> Error SubmissionProblem.Shape
                elif declaration.ReviewsFamilySequence then
                    match payment, family with
                    | None, Some sequence ->
                        Sequence.create sequence
                        |> Option.filter (fun value -> value >= Sequence.first)
                        |> Option.map (fun sequence ->
                            Ok(Binding.CaseErase(selection, expected, sequence)))
                        |> Option.defaultValue (
                            Error(
                                issue
                                    (target InputTarget.ExpectedFamilySequence)
                                    Violation.ExpectedRevisionRange
                            )
                        )
                    | _ -> Error SubmissionProblem.Shape
                elif payment.IsNone && family.IsNone then
                    Ok(Binding.Case(selection, expected))
                else
                    Error SubmissionProblem.Shape)
        | ActSubject.Work, OperationBinding.Work(selection, expected) ->
            version
                SubmissionDeclarations.creationRevision
                InputTarget.ExpectedWorkRevision
                expected
            |> Result.map (fun expected -> Binding.Work(selection, expected))
        | ActSubject.Note, OperationBinding.Note(selection, None) when declaration.Creates ->
            Ok(Binding.NoteNew selection)
        | ActSubject.Note, OperationBinding.Note(selection, Some id) when not declaration.Creates ->
            identity NoteId.create InputTarget.NoteId Violation.NoteIdRequired id
            |> Result.map (fun id -> Binding.Note(selection, id))
        | ActSubject.Reminder, OperationBinding.Reminder(selection, None) when declaration.Creates ->
            Ok(Binding.ReminderNew selection)
        | ActSubject.Reminder, OperationBinding.Reminder(selection, Some(id, expected)) when
            not declaration.Creates
            ->
            map2
                (fun id expected -> Binding.Reminder(selection, id, expected))
                (identity ReminderId.create InputTarget.ReminderId Violation.ReminderIdRequired id)
                (version
                    SubmissionDeclarations.existingRevision
                    InputTarget.ExpectedReminderRevision
                    expected)
        | ActSubject.People, OperationBinding.People(None, expected) when declaration.Creates ->
            creationVersion expected |> Result.map Binding.PeopleNew
        | ActSubject.People, OperationBinding.People(Some id, expected) when not declaration.Creates ->
            map2
                (fun id expected -> Binding.People(id, expected))
                (actorIdentity id)
                (version
                    SubmissionDeclarations.existingRevision
                    InputTarget.ExpectedRevision
                    expected)
        | _ -> Error SubmissionProblem.Shape

    let private declared task =
        ClaimCore.Domain.Tasks.Registry.findDeclaration task
        |> Option.filter (fun declaration -> declaration.Exposure = TaskExposure.Client)
        |> (function
        | Some value -> Ok value
        | None -> Error SubmissionProblem.Shape)

    let assemble (submission: SubmissionEnvelope<int64, Guid>) =
        declared submission.Task
        |> bind (fun declaration ->
            validate declaration.Inputs declaration.Collections submission.Inputs
            |> bind (fun () ->
                map2
                    (fun binding inputs ->
                        {
                            SubmittedOperation.Binding = binding
                            Inputs = inputs
                        })
                    (admitBinding declaration submission.Binding)
                    (mapInputs declaration.Collections submission.Inputs)))

    let admit envelope =
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

    let private domainIssues issues =
        let convert (issue: TaskInputIssue) =
            let input =
                match issue.Input with
                | TaskInputId.Target input -> input
                | TaskInputId.Scope -> InputTarget.Scope
                | TaskInputId.CaseId -> InputTarget.CaseId
                | _ -> invalidOp "A ledger task returned a query-only issue target."

            {
                InputIssue.Target = input
                Violation = issue.Violation
                RelatedDate = issue.RelatedDate
                GrantRow = issue.Row |> Option.bind (TaskRowTarget.ordinal >> GrantOrdinal.create)
            }

        issues |> List.map convert |> InputIssues.ofList

    let refusal =
        function
        | ClaimCore.Domain.Rejection.Refused refusal -> Some refusal
        | ClaimCore.Domain.Rejection.Input issues ->
            TaskInputIssues.toList issues |> domainIssues |> Option.map Refusal.InvalidInput
        | ClaimCore.Domain.Rejection.InvalidState _ -> None

    let private draftBinding declaration binding =
        let optional check =
            function
            | None -> Ok()
            | Some value -> check value |> Result.map ignore

        let revision minimum target = optional (version minimum target)

        let identifier create target violation =
            optional (identity create target violation)

        let family =
            optional (fun raw ->
                Sequence.create raw
                |> Option.filter (fun value -> value >= Sequence.first)
                |> Option.map Ok
                |> Option.defaultValue (
                    Error(
                        issue
                            (target InputTarget.ExpectedFamilySequence)
                            Violation.ExpectedRevisionRange
                    )
                ))

        match TaskDeclaration.bindingShape declaration, binding with
        | BindingShape.CaseNew, OperationBinding.CaseNew expected ->
            optional creationVersion expected
        | BindingShape.Case, OperationBinding.Case(_, expected, None, None) ->
            revision SubmissionDeclarations.existingRevision InputTarget.ExpectedRevision expected
        | BindingShape.CasePayment, OperationBinding.Case(_, expected, payment, None) ->
            collect
                [
                    revision
                        SubmissionDeclarations.existingRevision
                        InputTarget.ExpectedRevision
                        expected
                    identifier
                        OperationId.create
                        InputTarget.PaymentId
                        Violation.PaymentIdRequired
                        payment
                ]
        | BindingShape.CaseErase, OperationBinding.Case(_, expected, None, sequence) ->
            collect
                [
                    revision
                        SubmissionDeclarations.existingRevision
                        InputTarget.ExpectedRevision
                        expected
                    family sequence
                ]
        | BindingShape.Work, OperationBinding.Work(_, expected) ->
            revision
                SubmissionDeclarations.creationRevision
                InputTarget.ExpectedWorkRevision
                expected
        | BindingShape.ReminderNew, OperationBinding.Reminder(_, None)
        | BindingShape.NoteNew, OperationBinding.Note(_, None) -> Ok()
        | BindingShape.Reminder, OperationBinding.Reminder(_, Some(id, expected)) ->
            collect
                [
                    identity
                        ReminderId.create
                        InputTarget.ReminderId
                        Violation.ReminderIdRequired
                        id
                    |> Result.map ignore
                    revision
                        SubmissionDeclarations.existingRevision
                        InputTarget.ExpectedReminderRevision
                        expected
                ]
        | BindingShape.Note, OperationBinding.Note(_, note) ->
            identifier NoteId.create InputTarget.NoteId Violation.NoteIdRequired note
        | BindingShape.PeopleNew, OperationBinding.People(None, expected) ->
            optional creationVersion expected
        | BindingShape.People, OperationBinding.People(Some id, expected) ->
            collect
                [
                    actorIdentity id |> Result.map ignore
                    revision
                        SubmissionDeclarations.existingRevision
                        InputTarget.ExpectedRevision
                        expected
                ]
        | _ -> Error SubmissionProblem.Shape

    let private checkInputs (declaration: TaskDeclaration) inputs =
        let selected = ClaimCore.Domain.Tasks.Registry.find declaration.Id |> Option.get

        let issues =
            match selected with
            | ClaimCore.Domain.Tasks.Task.Family selected -> selected.Check inputs
            | ClaimCore.Domain.Tasks.Task.Person selected -> selected.Check inputs

        match domainIssues issues with
        | None -> Ok()
        | Some issues -> Error(SubmissionProblem.Rejected(Refusal.InvalidInput issues))

    let private operationIdentity =
        function
        | None -> Ok()
        | Some id ->
            identity OperationId.create InputTarget.OperationId Violation.OperationIdRequired id
            |> Result.map ignore

    let private syntaxResult =
        function
        | Error(SubmissionProblem.Rejected(Refusal.InvalidInput issues)) -> Ok(Some issues)
        | Error problem -> Error problem
        | Ok() -> Ok None

    let syntaxDraft (draft: SubmissionEnvelope<int64 option, Guid option>) =
        declared draft.Task
        |> bind (fun declaration ->
            let authored =
                validate declaration.Inputs declaration.Collections draft.Inputs
                |> bind (fun () -> mapInputs declaration.Collections draft.Inputs)
                |> bind (checkInputs declaration)

            collect
                [
                    operationIdentity draft.OperationId
                    draftBinding declaration draft.Binding
                    authored
                ]
            |> syntaxResult)

    let completeDraft (draft: SubmissionEnvelope<int64 option, Guid option>) =
        let binding =
            match draft.Binding with
            | OperationBinding.CaseNew(Some revision) -> Some(OperationBinding.CaseNew revision)
            | OperationBinding.Case(target, Some revision, payment, family) ->
                Some(OperationBinding.Case(target, revision, payment, family))
            | OperationBinding.Work(target, Some revision) ->
                Some(OperationBinding.Work(target, revision))
            | OperationBinding.Note(target, note) -> Some(OperationBinding.Note(target, note))
            | OperationBinding.Reminder(target, None) ->
                Some(OperationBinding.Reminder(target, None))
            | OperationBinding.Reminder(target, Some(id, Some revision)) ->
                Some(OperationBinding.Reminder(target, Some(id, revision)))
            | OperationBinding.People(actor, Some expected) ->
                Some(OperationBinding.People(actor, expected))
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
        let authored =
            match submission.Operation with
            | Error refusal -> Error(SubmissionProblem.Rejected refusal)
            | Ok operation ->
                declared submission.Task
                |> bind (fun declaration -> checkInputs declaration operation.Inputs)

        collect [ operationIdentity (Some submission.OperationId); authored ]
        |> syntaxResult
        |> Result.toOption
        |> Option.flatten

/// Pure draft acquisition policy shared by native interfaces and generated browser assembly.
module SubmissionAssembly =
    let private target input = TaskInputId.Target input
    let private reference = SubmissionDeclarations.caseReferenceInput

    /// Authored selection owns scope. Absence alone permits the installation default.
    let scope inputs =
        if Map.containsKey TaskInputId.Scope inputs then
            inputs
        else
            match
                SubmissionDeclarations.scopeRule
                    None
                    (Map.containsKey SubmissionDeclarations.caseIdInput inputs)
                    (Map.containsKey reference inputs)
            with
            | Some rule ->
                Map.add
                    SubmissionDeclarations.scopeInput
                    (SubmissionValue.Text(
                        if rule.Input.IsSome then
                            SubmissionDeclarations.caseScope
                        else
                            SubmissionDeclarations.installationScope
                    ))
                    inputs
            | None -> inputs // Keep contradictory authored alternatives for visible admission refusal.


    /// Missing selection is a draft placeholder; malformed or competing authored alternatives refuse.
    let caseSelection inputs =
        let invalid = Error SubmissionProblem.Shape

        match
            Map.tryFind SubmissionDeclarations.caseIdInput inputs, Map.tryFind reference inputs
        with
        | Some(SubmissionValue.Text value), None ->
            match Guid.TryParseExact(value, "D") with
            | true, value ->
                CaseId.create value
                |> Option.map (CaseSelector.Id >> Ok)
                |> Option.defaultValue invalid
            | _ -> invalid
        | None, Some(SubmissionValue.Text value) -> Ok(CaseSelector.Reference value)
        | None, None -> Ok(CaseSelector.Reference "")
        | _ -> invalid

    let defaults (declaration: TaskDeclaration) =
        declaration.Inputs
        |> List.choose (fun input ->
            match input.Input with
            | TaskInputId.Target(InputTarget.Group _) when input.Source = InputSource.Authored ->
                Some(
                    input.Input,
                    SubmissionValue.Text(CorrectionGroupAction.token CorrectionGroupAction.Keep)
                )
            | _ -> None)
        |> Map.ofList

    let withDefaults (declaration: TaskDeclaration) inputs =
        let values =
            Map.fold
                (fun values key value ->
                    if Map.containsKey key values then
                        values
                    else
                        Map.add key value values)
                inputs
                (defaults declaration)

        if declaration.Inputs |> List.exists (fun input -> input.Input = TaskInputId.Scope) then
            scope values
        else
            values

    /// UPDATE seeds are display hints even when a current control cannot edit them.
    /// Only declared non-UPDATE values outside Editable are immutable authored selections.
    let fixedInputs (declaration: TaskDeclaration) editable seeds =
        let editable = Set.ofList editable

        let declared =
            declaration.Inputs |> List.map (fun input -> input.Input, input) |> Map.ofList

        seeds
        |> Map.filter (fun key _ ->
            not (Set.contains key editable)
            && (Map.tryFind key declared
                |> Option.exists (fun input -> SubmissionDeclarations.fixedSeed input)))

    /// Fixed controls always win; editable UPDATE seeds never become authored values.
    let merge
        (editable: TaskInputId list)
        (fixedValues: Map<TaskInputId, SubmissionValue>)
        authored
        =
        let editable = Set.ofList editable

        Map.fold
            (fun values key value ->
                if Set.contains key editable then
                    values
                else
                    Map.add key value values)
            (Map.filter (fun key _ -> Set.contains key editable) authored)
            fixedValues

    let draftBinding (declaration: TaskDeclaration) selection revision identity =
        let plan = SubmissionDeclarations.binding declaration

        let expected =
            plan.Revision
            |> Option.bind (fun basis -> revision basis.Input |> Option.orElse basis.Initial)

        let selectedIdentity = plan.Identity |> Option.bind identity
        let family = plan.Family |> Option.bind (fun basis -> revision basis.Input)

        match plan.Shape with
        | BindingShape.CaseNew -> OperationBinding.CaseNew expected
        | BindingShape.Case -> OperationBinding.Case(selection, expected, None, None)
        | BindingShape.CasePayment ->
            OperationBinding.Case(selection, expected, selectedIdentity, None)
        | BindingShape.CaseErase -> OperationBinding.Case(selection, expected, None, family)
        | BindingShape.Work -> OperationBinding.Work(selection, expected)
        | BindingShape.NoteNew -> OperationBinding.Note(selection, None)
        | BindingShape.Note -> OperationBinding.Note(selection, selectedIdentity)
        | BindingShape.ReminderNew -> OperationBinding.Reminder(selection, None)
        | BindingShape.Reminder ->
            OperationBinding.Reminder(
                selection,
                selectedIdentity |> Option.map (fun id -> id, expected)
            )
        | BindingShape.PeopleNew -> OperationBinding.People(None, expected)
        | BindingShape.People -> OperationBinding.People(selectedIdentity, expected)

    /// Build a supplied control from explicit observed identity and revisions.
    let offeredBinding (declaration: TaskDeclaration) selection revision identity family =
        let required name =
            Option.defaultWith (fun () -> invalidArg name "A declared binding fact is required.")

        let expected () = required "revision" revision
        let selected () = required "selection" selection

        let identified create =
            identity |> Option.bind create |> required "identity"

        match (SubmissionDeclarations.binding declaration).Shape with
        | BindingShape.CaseNew -> Binding.CaseNew Revision.zero
        | BindingShape.Case -> Binding.Case(selected (), expected ())
        | BindingShape.CasePayment ->
            Binding.CasePayment(selected (), expected (), identified OperationId.create)
        | BindingShape.CaseErase ->
            Binding.CaseErase(selected (), expected (), required "family" family)
        | BindingShape.Work -> Binding.Work(selected (), expected ())
        | BindingShape.NoteNew -> Binding.NoteNew(selected ())
        | BindingShape.Note -> Binding.Note(selected (), identified NoteId.create)
        | BindingShape.ReminderNew -> Binding.ReminderNew(selected ())
        | BindingShape.Reminder ->
            Binding.Reminder(selected (), identified ReminderId.create, expected ())
        | BindingShape.PeopleNew -> Binding.PeopleNew Revision.zero
        | BindingShape.People -> Binding.People(identified ActorId.create, expected ())

    /// Complete only missing draft basis; an explicitly reviewed version is never refreshed.
    let completeBinding draft offered =
        match draft, offered with
        | OperationBinding.CaseNew expected, OperationBinding.CaseNew revision ->
            OperationBinding.CaseNew(Option.defaultValue revision expected)
        | OperationBinding.Case(target, expected, payment, family),
          OperationBinding.Case(_, revision, _, offeredFamily) ->
            OperationBinding.Case(
                target,
                Option.defaultValue revision expected,
                payment,
                Option.orElse offeredFamily family
            )
        | OperationBinding.Work(target, expected), OperationBinding.Work(_, revision) ->
            OperationBinding.Work(target, Option.defaultValue revision expected)
        | OperationBinding.People(id, expected), OperationBinding.People(_, revision) ->
            OperationBinding.People(id, Option.defaultValue revision expected)
        | OperationBinding.Reminder(target, Some(id, expected)),
          OperationBinding.Reminder(_, Some(_, revision)) ->
            OperationBinding.Reminder(target, Some(id, Option.defaultValue revision expected))
        | OperationBinding.Note(target, id), OperationBinding.Note _ ->
            OperationBinding.Note(target, id)
        | OperationBinding.Reminder(target, None), OperationBinding.Reminder(_, None) ->
            OperationBinding.Reminder(target, None)
        | _ -> invalidArg "offered" "A draft requires the matching offered binding."
