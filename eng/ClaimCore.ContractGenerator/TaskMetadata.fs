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
                choices =
                    TaskChoiceId.ofInput value.Input
                    |> Option.map TaskChoiceId.tokens
                    |> Option.defaultValue []
            |}

        TaskDeclarations.all
        |> List.map (fun task ->
            let kind, subject, act, endpoint, label, consequence =
                match task.Task with
                | TaskId.Operation act ->
                    "OPERATION",
                    Some(ActSubject.token (ActKind.subject act)),
                    Some(ActKind.token act),
                    ActorEndpoint.token (ActorEndpoint.Operation OperationEndpoint.Execute),
                    Vocabulary.messageKey ActKind.vocabulary act "label",
                    Some(Vocabulary.token Consequence.vocabulary (Consequence.ofAct act))
                | TaskId.Query query ->
                    let endpoint = ActorEndpoint.Query query

                    "QUERY",
                    None,
                    None,
                    ActorEndpoint.token endpoint,
                    Vocabulary.messageKey ActorEndpoint.vocabulary endpoint "label",
                    None

                | TaskId.Command command ->
                    let endpoint = ActorEndpoint.Command command

                    "COMMAND",
                    None,
                    None,
                    ActorEndpoint.token endpoint,
                    Vocabulary.messageKey ActorEndpoint.vocabulary endpoint "label",
                    None

            {|
                id = TaskId.key task.Task
                kind = kind
                subject = subject
                act = act
                endpoint = endpoint
                labelId = label
                meaningId =
                    match task.Task with
                    | TaskId.Operation act ->
                        Vocabulary.messageKey ActKind.vocabulary act "meaning"
                    | TaskId.Query query ->
                        Vocabulary.messageKey
                            ActorEndpoint.vocabulary
                            (ActorEndpoint.Query query)
                            "meaning"
                    | TaskId.Command command ->
                        Vocabulary.messageKey
                            ActorEndpoint.vocabulary
                            (ActorEndpoint.Command command)
                            "meaning"
                consequence = consequence
                inputs = task.Inputs |> List.map input
                collections =
                    task.Collections
                    |> List.map (fun collection ->
                        {|
                            id = TaskCollectionId.key collection.Collection
                            input =
                                TaskInputId.token (TaskCollectionId.input collection.Collection)
                            minimumRows = TaskCollectionId.minimumRows collection.Collection
                            maximumRows = TaskCollectionId.maximumRows collection.Collection
                            inputs = collection.Inputs |> List.map input
                        |})
            |})
        |> JsonSerializer.Serialize

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
