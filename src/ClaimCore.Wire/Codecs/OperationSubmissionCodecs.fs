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
            input.Mode <> TaskInputMode.Required || input.DefaultText.IsSome,
            scalar input)
        |> fieldMapWithPresence

    let private inputs (declaration: ClientTaskDeclaration) =
        let members =
            declaration.Inputs
            |> List.filter (fun input -> input.Source = InputSource.Authored)
            |> List.map (fun input ->
                input.Input,
                TaskInputId.token input.Input,
                input.Mode <> TaskInputMode.Required || input.DefaultText.IsSome,
                scalar input)

        let collections =
            declaration.Collections
            |> List.map (fun collection ->
                let input = TaskCollectionId.input collection.Collection

                let codec =
                    inputMap collection.Inputs
                    |> boundedList
                        (collection.MinimumRows)
                        (TaskCollectionId.maximumRows collection.Collection)
                    |> map SubmissionValue.Rows (function
                        | SubmissionValue.Rows rows -> rows
                        | _ -> invalidOp "Collection task input required.")

                input, TaskInputId.token input, collection.MinimumRows = 0, codec)

        fieldMapWithPresence (members @ collections)

    let private caseTarget =
        function
        | OperationBinding.Case(target, _, _, _) -> target
        | _ -> invalidOp "Case binding required."

    let private caseRevision =
        function
        | OperationBinding.Case(_, revision, _, _) -> revision
        | _ -> invalidOp "Case binding required."

    let private paymentId =
        function
        | OperationBinding.Case(_, _, payment, _) -> selected "Payment selection required." payment
        | _ -> invalidOp "Case binding required."

    let private workTarget =
        function
        | OperationBinding.Work(target, _) -> target
        | _ -> invalidOp "Work binding required."

    let private workRevision =
        function
        | OperationBinding.Work(_, revision) -> revision
        | _ -> invalidOp "Work binding required."

    let private partyTarget =
        function
        | OperationBinding.Party(target, _) -> target
        | _ -> invalidOp "Party binding required."

    let private noteTarget =
        function
        | OperationBinding.Note(target, _) -> target
        | _ -> invalidOp "Note binding required."

    let private noteId =
        function
        | OperationBinding.Note(_, note) -> selected "Note selection required." note
        | _ -> invalidOp "Note binding required."

    let private linkTarget =
        function
        | OperationBinding.Link(target, _) -> target
        | _ -> invalidOp "Link binding required."

    let private linkId =
        function
        | OperationBinding.Link(_, link) -> selected "Link selection required." link
        | _ -> invalidOp "Link binding required."

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
        | OperationBinding.People(actor, _) -> selected "Actor selection required." actor
        | _ -> invalidOp "People binding required."

    let binding kind =
        let declaration =
            TaskId.token kind
            |> TaskId.ofLiteral
            |> ClaimCore.Domain.Tasks.Registry.findDeclaration
            |> Option.defaultWith (fun () -> invalidArg (nameof kind) "The task is not declared.")

        let plan = SubmissionDeclarations.binding declaration

        let bounds input =
            let minimum, maximum =
                SubmissionDeclarations.integerBounds declaration (TaskInputId.Target input)

            integer minimum maximum

        let version =
            bounds (
                plan.Revision
                |> Option.map _.Input
                |> Option.defaultValue InputTarget.ExpectedRevision
            )

        let binds input =
            declaration.Inputs
            |> List.exists (fun item ->
                item.Input = TaskInputId.Target input && item.Source = InputSource.Binding)

        let built =
            match declaration.Target with
            | ActSubject.Case when declaration.Creates ->
                record OperationBinding.CaseNew
                |> fixedField "kind" "CASE"
                |> field
                    "expectedRevision"
                    (function
                    | OperationBinding.CaseNew revision -> revision
                    | _ -> invalidOp "New case binding required.")
                    version
                |> build
            | ActSubject.Case when binds InputTarget.PaymentId ->
                record (fun target revision payment ->
                    OperationBinding.Case(target, revision, Some payment, None))
                |> fixedField "kind" "CASE"
                |> field "target" caseTarget RequestCodecs.caseSelector
                |> field "expectedRevision" caseRevision version
                |> field "paymentId" paymentId uuid
                |> build
            | ActSubject.Case when declaration.ReviewsFamilySequence ->
                record (fun target revision family ->
                    OperationBinding.Case(target, revision, None, Some family))
                |> fixedField "kind" "CASE"
                |> field "target" caseTarget RequestCodecs.caseSelector
                |> field "expectedRevision" caseRevision version
                |> field
                    "expectedFamilySequence"
                    (function
                    | OperationBinding.Case(_, _, _, Some family) -> family
                    | _ -> invalidOp "Erasure basis required.")
                    (bounds InputTarget.ExpectedFamilySequence)
                |> build
            | ActSubject.Case ->
                record (fun target revision -> OperationBinding.Case(target, revision, None, None))
                |> fixedField "kind" "CASE"
                |> field "target" caseTarget RequestCodecs.caseSelector
                |> field "expectedRevision" caseRevision version
                |> build
            | ActSubject.Work ->
                record (fun target revision -> OperationBinding.Work(target, revision))
                |> fixedField "kind" "WORK"
                |> field "target" workTarget RequestCodecs.caseSelector
                |> field "expectedRevision" workRevision version
                |> build
            | ActSubject.Note when declaration.Creates ->
                record (fun target -> OperationBinding.Note(target, None))
                |> fixedField "kind" "NOTE"
                |> field "target" noteTarget RequestCodecs.caseSelector
                |> build
            | ActSubject.Note ->
                record (fun target note -> OperationBinding.Note(target, Some note))
                |> fixedField "kind" "NOTE"
                |> field "target" noteTarget RequestCodecs.caseSelector
                |> field "noteId" noteId uuid
                |> build
            | ActSubject.Party when declaration.Creates ->
                record (fun target -> OperationBinding.Party(target, None))
                |> fixedField "kind" "PARTY"
                |> field "target" partyTarget RequestCodecs.caseSelector
                |> build
            | ActSubject.Party ->
                record (fun target id revision ->
                    OperationBinding.Party(target, Some(id, revision)))
                |> fixedField "kind" "PARTY"
                |> field "target" partyTarget RequestCodecs.caseSelector
                |> field
                    "partyId"
                    (function
                    | OperationBinding.Party(_, current) ->
                        (selected "Party selection required." current) |> fst
                    | _ -> invalidOp "Party binding required.")
                    uuid
                |> field
                    "expectedRevision"
                    (function
                    | OperationBinding.Party(_, current) ->
                        (selected "Party selection required." current) |> snd
                    | _ -> invalidOp "Party binding required.")
                    version
                |> build
            | ActSubject.Link when declaration.Creates ->
                record (fun target -> OperationBinding.Link(target, None))
                |> fixedField "kind" "LINK"
                |> field "target" linkTarget RequestCodecs.caseSelector
                |> build
            | ActSubject.Link ->
                record (fun target link -> OperationBinding.Link(target, Some link))
                |> fixedField "kind" "LINK"
                |> field "target" linkTarget RequestCodecs.caseSelector
                |> field "linkId" linkId uuid
                |> build
            | ActSubject.Reminder when declaration.Creates ->
                record (fun target -> OperationBinding.Reminder(target, None))
                |> fixedField "kind" "REMINDER"
                |> field "target" reminderTarget RequestCodecs.caseSelector
                |> build
            | ActSubject.Reminder ->
                record (fun target id revision ->
                    OperationBinding.Reminder(target, Some(id, revision)))
                |> fixedField "kind" "REMINDER"
                |> field "target" reminderTarget RequestCodecs.caseSelector
                |> field "reminderId" (reminderCurrent >> fst) uuid
                |> field "expectedRevision" (reminderCurrent >> snd) version
                |> build
            | ActSubject.People when declaration.Creates ->
                record (fun revision -> OperationBinding.People(None, revision))
                |> fixedField "kind" "PEOPLE"
                |> field
                    "expectedRevision"
                    (function
                    | OperationBinding.People(_, revision) -> revision
                    | _ -> invalidOp "People binding required.")
                    version
                |> build
            | ActSubject.People ->
                record (fun actor revision -> OperationBinding.People(Some actor, revision))
                |> fixedField "kind" "PEOPLE"
                |> field "actorId" actorId uuid
                |> field
                    "expectedRevision"
                    (function
                    | OperationBinding.People(_, revision) -> revision
                    | _ -> invalidOp "People binding required.")
                    version
                |> build

        let shape =
            match TaskDeclaration.bindingShape declaration with
            | BindingShape.CaseNew -> "CASE.new"
            | BindingShape.Case -> "CASE"
            | BindingShape.CasePayment -> "CASE.payment"
            | BindingShape.CaseErase -> "CASE.erase"
            | BindingShape.Work -> "WORK"
            | BindingShape.ReminderNew -> "REMINDER.new"
            | BindingShape.Reminder -> "REMINDER"
            | BindingShape.NoteNew -> "NOTE.new"
            | BindingShape.Note -> "NOTE"
            | BindingShape.PartyNew -> "PARTY.new"
            | BindingShape.Party -> "PARTY"
            | BindingShape.LinkNew -> "LINK.new"
            | BindingShape.Link -> "LINK"
            | BindingShape.PeopleNew -> "PEOPLE.new"
            | BindingShape.People -> "PEOPLE"

        built |> named ("Binding." + shape)

    let submission =
        let variants =
            TaskVocabulary.client
            |> List.map (fun kind ->
                let declaration = TaskDeclarations.describe (TaskId.token kind)

                let fields =
                    record (fun operation binding values -> operation, binding, values)
                    |> field "operationId" (fun (id, _, _) -> id) uuid
                    |> field "binding" (fun (_, binding, _) -> binding) (binding kind)
                    |> field
                        "inputs"
                        (fun (_, _, values) -> values)
                        (inputs declaration
                         |> named (
                             if
                                 declaration.Collections.IsEmpty
                                 && (declaration.Inputs
                                     |> List.forall (fun input ->
                                         input.Source <> InputSource.Authored))
                             then
                                 "Empty"
                             else
                                 "OperationInputs." + TaskId.token kind
                         ))

                variant
                    (TaskId.token kind)
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
                    fields)

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
