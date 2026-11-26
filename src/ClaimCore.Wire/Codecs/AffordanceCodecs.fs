namespace ClaimCore.Wire

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Presentation

module AffordanceCodecs =
    open Codec
    open ResponseScalars
    open PresentedMessageCodecs

    [<Literal>]
    // Preserve the shared action grammar bound.
    let MaximumActions = 46

    let affordance =
        let inputVocabulary =
            Vocabulary.define TaskInputId.vocabularyName TaskInputId.all TaskInputId.token

        let binding (declaration: ClientTaskDeclaration) =
            match declaration.Kind with
            | ClientTaskKind.Operation ->
                let task = declaration.Ledger |> Option.get

                OperationSubmissionCodecs.binding task.Id
                |> interpret
                    (fun path raw ->
                        OperationSubmission.admitBinding task raw
                        |> Result.mapError (fun _ ->
                            {
                                Path = path
                                Reason = "Invalid reviewed binding."
                            }))
                    OperationSubmission.rawBinding
                |> map ActionControl.Operation (function
                    | ActionControl.Operation value -> value
                    | _ -> invalidOp "Operation control required.")
            | ClientTaskKind.Command ->
                let command = CommandTaskId.parse declaration.Id |> Option.get

                Codec.tagged
                    ("CommandBinding." + declaration.Id)
                    [
                        variant
                            "COMMAND"
                            ActionControl.Command
                            (function
                            | ActionControl.Command value -> Some value
                            | _ -> None)
                            (record id |> field "request" id (CommandControlCodecs.forTask command))
                    ]
            | ClientTaskKind.Query
            | ClientTaskKind.Binary -> invalidOp "A read is not an action control."

        let seeds =
            TaskInputId.all
            |> List.map (fun input ->
                input, TaskInputId.token input, true, ClaimCore.RecordFormat.InputMapCodecs.atom)
            |> fieldMapWithPresence
            |> named "AffordanceSeeds"

        let choices =
            TaskInputId.all
            |> List.choose (fun input ->
                TaskChoiceId.ofInput input
                |> Option.map (fun choice ->
                    let tokens = TaskChoiceId.tokens choice

                    input,
                    TaskInputId.token input,
                    true,
                    list
                        tokens.Length
                        (Vocabulary.define
                            (Vocabulary.token TaskChoiceId.vocabulary choice)
                            tokens
                            id
                         |> VocabularyCodec.create)))
            |> fieldMapWithPresence
            |> named "AffordanceChoices"

        let fields (declaration: ClientTaskDeclaration) =
            let task = TaskId.ofLiteral declaration.Id
            let editableInputs = declaration.EditableInputs

            let editable =
                inputVocabulary
                |> VocabularyCodec.select (fun input -> List.contains input editableInputs)
                |> list editableInputs.Length

            let emptyMap: Codec<Map<TaskInputId, InputValue>> = record Map.empty |> build
            let emptyChoices: Codec<Map<TaskInputId, string list>> = record Map.empty |> build

            (record (fun label shortLabel binding seeds editable choices blocked ->
                {
                    Affordance.Task = task
                    LabelId = label
                    ShortLabelId = shortLabel
                    Control = binding
                    Seeds = seeds
                    Editable = editable
                    Choices = choices
                    Blocked = blocked
                })
            : Fields<Affordance, _>)
            |> field "labelId" _.LabelId (boundedText 1 160)
            |> optionalField "shortLabelId" _.ShortLabelId (boundedText 1 160)
            |> field "binding" _.Control (binding declaration)
            |> field
                "seeds"
                _.Seeds
                (if declaration.Kind = ClientTaskKind.Command then
                     emptyMap
                 else
                     seeds)
            |> field "editable" _.Editable editable
            |> field
                "choices"
                _.Choices
                (if declaration.Kind = ClientTaskKind.Command then
                     emptyChoices
                 else
                     choices)
            |> field "blocked" _.Blocked (nullable advice)

        TaskDeclarations.all
        |> List.filter (fun declaration ->
            declaration.Kind = ClientTaskKind.Operation
            || declaration.Kind = ClientTaskKind.Command)
        |> List.map (fun declaration ->
            variant
                declaration.Id
                id
                (fun (value: Affordance) ->
                    if TaskId.token value.Task = declaration.Id then
                        Some value
                    else
                        None)
                (fields declaration))
        |> taggedOn "task" "Affordance"
        |> interpret
            (fun path value ->
                if Affordance.valid value then
                    Ok value
                else
                    Error
                        {
                            Path = path
                            Reason = "Undeclared input controls or mismatched reviewed binding."
                        })
            id
        |> named "Affordance"

    let actions = list MaximumActions affordance
