namespace ClaimCore.Wire

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

/// Task declarations own closed authored maps; typed bindings contain selection and version only.
module OperationSubmissionCodecs =
    open Codec

    let private selected describe =
        function
        | Some value -> value
        | None -> invalidOp describe

    let private scalar input =
        let value =
            match input.Atom with
            | TaskAtom.Text ->
                let codec =
                    TaskChoiceId.ofInput input.Input
                    |> Option.map (fun choice ->
                        Vocabulary.define
                            (Vocabulary.token TaskChoiceId.vocabulary choice)
                            (TaskChoiceId.tokens choice)
                            id
                        |> VocabularyCodec.create)
                    |> Option.defaultValue text

                codec
                |> map SubmissionValue.Text (function
                    | SubmissionValue.Text value -> value
                    | _ -> invalidOp "Text task input required.")
            | TaskAtom.Integer ->
                integer (-SafeInteger.Maximum) SafeInteger.Maximum
                |> map SubmissionValue.Integer (function
                    | SubmissionValue.Integer value -> value
                    | _ -> invalidOp "Integer task input required.")
            | TaskAtom.Boolean ->
                boolean
                |> map SubmissionValue.Boolean (function
                    | SubmissionValue.Boolean value -> value
                    | _ -> invalidOp "Boolean task input required.")

        nullable value
        |> map (Option.defaultValue SubmissionValue.Clear) (function
            | SubmissionValue.Clear -> None
            | value -> Some value)

    let private inputMap (inputs: TaskInputDeclaration list) =
        inputs
        |> List.map (fun input ->
            input.Input,
            TaskInputId.token input.Input,
            input.Mode <> TaskInputMode.Required,
            scalar input)
        |> fieldMapWithPresence

    let private inputs (declaration: TaskDeclaration) =
        let members =
            declaration.Inputs
            |> List.filter (fun input -> input.Source = InputSource.Authored)
            |> List.map (fun input ->
                input.Input,
                TaskInputId.token input.Input,
                input.Mode <> TaskInputMode.Required,
                scalar input)

        let collections =
            declaration.Collections
            |> List.map (fun collection ->
                let input = TaskCollectionId.input collection.Collection

                let codec =
                    inputMap collection.Inputs
                    |> boundedList
                        (TaskCollectionId.minimumRows collection.Collection)
                        (TaskCollectionId.maximumRows collection.Collection)
                    |> map SubmissionValue.Rows (function
                        | SubmissionValue.Rows rows -> rows
                        | _ -> invalidOp "Collection task input required.")

                input, TaskInputId.token input, false, codec)

        fieldMapWithPresence (members @ collections)

    /// Prefill is partial, including collection rows that the user has not completed yet.
    let seedInputs (declaration: TaskDeclaration) =
        let members =
            declaration.Inputs
            |> List.map (fun input ->
                input.Input, TaskInputId.token input.Input, true, scalar input)

        let collections =
            declaration.Collections
            |> List.map (fun collection ->
                let input = TaskCollectionId.input collection.Collection

                let row =
                    collection.Inputs
                    |> List.map (fun input ->
                        input.Input, TaskInputId.token input.Input, true, scalar input)
                    |> fieldMapWithPresence

                let codec =
                    boundedList 0 (TaskCollectionId.maximumRows collection.Collection) row
                    |> map SubmissionValue.Rows (function
                        | SubmissionValue.Rows rows -> rows
                        | _ -> invalidOp "Collection seed required.")

                input, TaskInputId.token input, true, codec)

        fieldMapWithPresence (members @ collections)

    let private caseTarget =
        function
        | OperationBinding.Case(target, _, _) -> target
        | _ -> invalidOp "Case binding required."

    let private caseRevision =
        function
        | OperationBinding.Case(_, revision, _) -> revision
        | _ -> invalidOp "Case binding required."

    let private paymentId =
        function
        | OperationBinding.Case(_, _, payment) -> selected "Payment selection required." payment
        | _ -> invalidOp "Case binding required."

    let private workTarget =
        function
        | OperationBinding.Work(target, _) -> target
        | _ -> invalidOp "Work binding required."

    let private workRevision =
        function
        | OperationBinding.Work(_, revision) -> revision
        | _ -> invalidOp "Work binding required."

    let private noteTarget =
        function
        | OperationBinding.Note(target, _) -> target
        | _ -> invalidOp "Note binding required."

    let private noteId =
        function
        | OperationBinding.Note(_, note) -> selected "Note selection required." note
        | _ -> invalidOp "Note binding required."

    let private reminderTarget =
        function
        | OperationBinding.Reminder(target, _) -> target
        | _ -> invalidOp "Reminder binding required."

    let private reminderCurrent =
        function
        | OperationBinding.Reminder(_, current) -> selected "Reminder selection required." current
        | _ -> invalidOp "Reminder binding required."

    let private actorId =
        function
        | OperationBinding.People actor -> selected "Actor selection required." actor
        | _ -> invalidOp "People binding required."

    let binding kind =
        let version = integer 0L Revision.Maximum
        let positiveVersion = integer 1L Revision.Maximum

        let built =
            match kind with
            | ActKind.Case CaseActKind.RemovePayment ->
                record (fun target revision payment ->
                    OperationBinding.Case(target, revision, Some payment))
                |> fixedField "kind" "CASE"
                |> field "target" caseTarget RequestCodecs.caseSelector
                |> field "expectedRevision" caseRevision version
                |> field "paymentId" paymentId uuid
                |> build
            | ActKind.Case task ->
                record (fun target revision -> OperationBinding.Case(target, revision, None))
                |> fixedField "kind" "CASE"
                |> field "target" caseTarget RequestCodecs.caseSelector
                |> field
                    "expectedRevision"
                    caseRevision
                    (if task = CaseActKind.Open then integer 0L 0L else version)
                |> build
            | ActKind.Work _ ->
                record (fun target revision -> OperationBinding.Work(target, revision))
                |> fixedField "kind" "WORK"
                |> field "target" workTarget RequestCodecs.caseSelector
                |> field "expectedRevision" workRevision version
                |> build
            | ActKind.Note NoteActKind.AddNote ->
                record (fun target -> OperationBinding.Note(target, None))
                |> fixedField "kind" "NOTE"
                |> field "target" noteTarget RequestCodecs.caseSelector
                |> build
            | ActKind.Note NoteActKind.RemoveNote ->
                record (fun target note -> OperationBinding.Note(target, Some note))
                |> fixedField "kind" "NOTE"
                |> field "target" noteTarget RequestCodecs.caseSelector
                |> field "noteId" noteId uuid
                |> build
            | ActKind.Reminder ReminderActKind.Create ->
                record (fun target -> OperationBinding.Reminder(target, None))
                |> fixedField "kind" "REMINDER"
                |> field "target" reminderTarget RequestCodecs.caseSelector
                |> build
            | ActKind.Reminder _ ->
                record (fun target id revision ->
                    OperationBinding.Reminder(target, Some(id, revision)))
                |> fixedField "kind" "REMINDER"
                |> field "target" reminderTarget RequestCodecs.caseSelector
                |> field "reminderId" (reminderCurrent >> fst) uuid
                |> field "expectedRevision" (reminderCurrent >> snd) positiveVersion
                |> build
            | ActKind.People(PeopleActKind.Register | PeopleActKind.Admit) ->
                record (OperationBinding.People None) |> fixedField "kind" "PEOPLE" |> build
            | ActKind.People _ ->
                record (Some >> OperationBinding.People)
                |> fixedField "kind" "PEOPLE"
                |> field "actorId" actorId uuid
                |> build

        built |> named ("OperationBinding." + ActKind.token kind)

    let submission =
        let variants =
            TaskId.all
            |> List.choose (function
                | TaskId.Operation kind ->
                    let declaration = TaskDeclarations.describe (TaskId.Operation kind)

                    let fields =
                        record (fun operation binding values -> operation, binding, values)
                        |> field "operationId" (fun (id, _, _) -> id) uuid
                        |> field "binding" (fun (_, binding, _) -> binding) (binding kind)
                        |> field
                            "inputs"
                            (fun (_, _, values) -> values)
                            (inputs declaration |> named ("OperationInputs." + ActKind.token kind))

                    variant
                        (ActKind.token kind)
                        (fun (operation, binding, values) ->
                            {
                                SubmissionEnvelope.OperationId = operation
                                Task = kind
                                Binding = binding
                                Inputs = values
                            })
                        (fun (submission: SubmissionEnvelope<int64, System.Guid>) ->
                            if submission.Task = kind then
                                Some(submission.OperationId, submission.Binding, submission.Inputs)
                            else
                                None)
                        fields
                    |> Some
                | _ -> None)

        taggedOn "task" "OperationSubmission" variants
        |> interpret
            (fun path envelope ->
                OperationSubmission.admit envelope
                |> Result.mapError (fun () ->
                    {
                        Path = path
                        Reason = "Invalid declared submission."
                    }))
            (fun (submission: OperationSubmission) -> submission.Envelope)
