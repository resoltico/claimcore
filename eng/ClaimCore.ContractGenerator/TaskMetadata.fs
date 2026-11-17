namespace ClaimCore.ContractGeneration

open System.Text.Json
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire

/// Emit declaration metadata for generic client acquisition.
module TaskMetadata =
    let internal declarationSource () =
        let input (value: TaskInputDeclaration) =
            {|
                id = TaskInputId.token value.Input
                mode = TaskInputMode.token value.Mode
                source = InputSource.token value.Source
                clearable = value.Clearable
                atom = string value.Atom |> _.ToUpperInvariant()
                multiple = value.Multiple
                retention = InputRetention.token value.Retention
                choicesFrom = value.ChoicesFrom
                choicesWhen =
                    value.ChoicesWhen
                    |> List.map (fun choices ->
                        {|
                            messageId = TaskInputPresentation.choiceConstraintMessage choices.Input
                            input = TaskInputId.token choices.Input
                            value = choices.Value
                            choices = choices.Choices
                        |})
                choices =
                    TaskChoiceId.ofInput value.Input
                    |> Option.map TaskChoiceId.tokens
                    |> Option.defaultValue []
            |}

        let atom (value: InputValue) : obj | null =
            match value with
            | InputValue.Text value -> box value
            | InputValue.Integer value -> box value
            | InputValue.Boolean value -> box value
            | InputValue.Null -> null

        let definition id kind subject endpoint consequence creates paged csv inputs collections =
            let prefix = if kind = "OPERATION" then "task." else "endpoint."

            {|
                id = id
                kind = kind
                subject = subject
                endpoint = endpoint
                labelId = prefix + id + ".label"
                meaningId = prefix + id + ".meaning"
                consequence = consequence
                creates = creates
                paged = paged
                csv = csv
                defaults =
                    (if kind = "OPERATION" then
                         ClaimCore.Domain.Tasks.Registry.findDeclaration (TaskId.ofLiteral id)
                     else
                         None)
                    |> Option.map SubmissionAssembly.defaults
                    |> Option.defaultValue Map.empty
                    |> Map.toList
                    |> List.map (fun (key, value) ->
                        let atom: obj | null =
                            match value with
                            | SubmissionValue.Text value -> box value
                            | SubmissionValue.Integer value -> box value
                            | SubmissionValue.Boolean value -> box value
                            | SubmissionValue.Clear -> null
                            | SubmissionValue.Rows _ ->
                                invalidOp "Scalar defaults cannot contain rows."

                        TaskInputId.token key, atom)
                    |> Map.ofList
                inputs = inputs |> List.map input
                collections =
                    collections
                    |> List.map (fun (collection: TaskCollectionDeclaration) ->
                        {|
                            id = TaskCollectionId.key collection.Collection
                            input =
                                TaskInputId.token (TaskCollectionId.input collection.Collection)
                            minimumRows = collection.MinimumRows
                            maximumRows = TaskCollectionId.maximumRows collection.Collection
                            defaults =
                                TaskCollectionId.defaultValues collection.Collection
                                |> Map.toList
                                |> List.map (fun (input, value) ->
                                    TaskInputId.token input, atom value)
                                |> Map.ofList
                            inputs = collection.Inputs |> List.map input
                        |})
            |}

        let operations =
            ClaimCore.Domain.Tasks.Registry.clientDeclarations
            |> List.map (fun declaration ->
                definition
                    (TaskId.token declaration.Id)
                    "OPERATION"
                    (Some(ActSubject.token declaration.Target))
                    (ActorEndpoint.token (ActorEndpoint.Operation OperationEndpoint.Execute))
                    (Some(Vocabulary.token Consequence.vocabulary declaration.Consequence))
                    declaration.Creates
                    false
                    false
                    declaration.Inputs
                    declaration.Collections)

        let endpoints =
            EndpointDeclarations.all
            |> List.map (fun declaration ->
                definition
                    declaration.Id
                    (match declaration.Kind with
                     | EndpointKind.Query -> "QUERY"
                     | EndpointKind.Command -> "COMMAND")
                    None
                    declaration.Id
                    None
                    false
                    declaration.Paged
                    declaration.Csv
                    declaration.Inputs
                    [])

        operations @ endpoints |> JsonSerializer.Serialize

    let internal inputPresentationSource () =
        TaskInputId.all
        |> List.map (fun input ->
            let kind, group =
                match input with
                | TaskInputId.Target(InputTarget.Field field) ->
                    "FIELD", (BusinessField.describe field).Group
                | TaskInputId.Target(InputTarget.Group group) -> "GROUP", Some group
                | _ -> "INPUT", None

            {|
                id = TaskInputId.token input
                key = TaskInputId.key input
                kind = kind
                group = group |> Option.map RecordGroup.token
                labelId = TaskInputPresentation.messageKey input "label"
                hintId = TaskInputPresentation.hintId input
                vocabulary = (TaskChoiceId.ofInput input |> Option.map TaskChoiceId.vocabularyName)
                ruleId = TaskInputPresentation.messageKey input "rule"
            |})
        |> JsonSerializer.Serialize

    let source () =
        [
            "export const clientCompositions = "
            + JsonSerializer.Serialize(
                {|
                    accountRegistration = TaskId.token ClientCompositions.accountRegistration
                    admission = TaskId.token ClientCompositions.admission
                    noteEntry = TaskId.token ClientCompositions.noteEntry
                    assignment = TaskId.token ClientCompositions.assignment
                    reminderCreation = TaskId.token ClientCompositions.reminderCreation
                |}
            )
            + " as const;"
            "export const taskDefinitions = " + declarationSource () + " as const;"
            "export type TaskId = (typeof taskDefinitions)[number]['id'];"
            "export type TaskDefinition = (typeof taskDefinitions)[number];"
            "export type OperationTaskDefinition = Extract<TaskDefinition,{readonly kind:'OPERATION'}>;"
            "export type OperationTaskId = OperationTaskDefinition['id'];"
            "export const taskInputDefinitions = "
            + inputPresentationSource ()
            + " as const;"
            "export type TaskInputDefinition = (typeof taskInputDefinitions)[number];"
            "export const taskChoices = "
            + (TaskChoiceId.all
               |> List.map (fun choice ->
                   Vocabulary.token TaskChoiceId.vocabulary choice, TaskChoiceId.tokens choice)
               |> Map.ofList
               |> JsonSerializer.Serialize)
            + " as const;"
        ]
        |> String.concat "\n"
