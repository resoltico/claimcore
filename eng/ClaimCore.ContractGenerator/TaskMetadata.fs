namespace ClaimCore.ContractGeneration

open System.Text.Json
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire

/// Emit declaration metadata for generic client acquisition.
module TaskMetadata =
    let internal declarationSource () =
        let input declarations (value: TaskInputDeclaration) =
            {|
                id = TaskInputId.token value.Input
                mode = TaskInputMode.token value.Mode
                source = InputSource.token value.Source
                clearable = value.Clearable
                fixedSeed = SubmissionDeclarations.fixedSeed value
                activeWhen =
                    SubmissionDeclarations.activation declarations value
                    |> Option.map (fun (input, selected) ->
                        {|
                            input = TaskInputId.token input
                            value = selected
                        |})
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

        let definition
            (ledger: TaskDeclaration option)
            id
            kind
            (labelId, meaningId)
            subject
            endpoint
            consequence
            creates
            paged
            csv
            inputs
            collections
            =
            {|
                id = id
                kind = kind
                subject = subject
                endpoint = endpoint
                labelId = labelId
                meaningId = meaningId
                consequence = consequence
                creates = creates
                review =
                    ledger
                    |> Option.map (fun declaration ->
                        TaskReview.mode declaration.Id |> Vocabulary.token TaskReview.vocabulary)
                paged = paged
                csv = csv
                execution =
                    ledger
                    |> Option.map (fun declaration ->
                        match declaration.Execution with
                        | TaskExecution.Structured -> "JSON"
                        | TaskExecution.AttachmentUpload -> "ATTACHMENT_UPLOAD")
                binding =
                    ledger
                    |> Option.map (fun declaration ->
                        let plan = SubmissionDeclarations.binding declaration

                        let revision (value: SubmissionRevision) =
                            {|
                                input = TaskInputId.token (TaskInputId.Target value.Input)
                                minimum = value.Minimum
                                maximum = value.Maximum
                                initial = value.Initial
                            |}

                        {|
                            kind = ActSubject.token plan.Subject
                            selectsCase = plan.SelectsCase
                            revision = plan.Revision |> Option.map revision
                            identity =
                                plan.Identity
                                |> Option.map (fun input ->
                                    {|
                                        input = TaskInputId.token input
                                        memberName = TaskInputId.key input
                                    |})
                            family = plan.Family |> Option.map revision
                        |})
                defaults =
                    ledger
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
                inputs = inputs |> List.map (input inputs)
                collections =
                    collections
                    |> List.map (fun (collection: TaskCollectionDeclaration) ->
                        {|
                            id = TaskCollectionId.key collection.Collection
                            labelId = CollectionPresentation.labelId collection.Collection
                            itemLabelId = CollectionPresentation.itemLabelId collection.Collection
                            addLabelId = CollectionPresentation.addLabelId collection.Collection
                            removeLabelId =
                                CollectionPresentation.removeLabelId collection.Collection
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
                            inputs = collection.Inputs |> List.map (input collection.Inputs)
                        |})
            |}

        let operations =
            ClaimCore.Domain.Tasks.Registry.clientDeclarations
            |> List.map (fun declaration ->
                let id = TaskId.token declaration.Id

                definition
                    (Some declaration)
                    id
                    "OPERATION"
                    ("task." + id + ".label", "task." + id + ".meaning")
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
                    None
                    declaration.Id
                    (match declaration.Kind with
                     | EndpointKind.Query -> "QUERY"
                     | EndpointKind.Command -> "COMMAND"
                     | EndpointKind.Binary -> "BINARY")
                    (declaration.LabelId, declaration.MeaningId)
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
                    attachmentEntry = TaskId.token ClientCompositions.attachmentEntry
                    attachmentWithdrawal = TaskId.token ClientCompositions.attachmentWithdrawal
                    assignment = TaskId.token ClientCompositions.assignment
                    reminderCreation = TaskId.token ClientCompositions.reminderCreation
                |}
            )
            + " as const;"
            "export const submissionSelection = "
            + JsonSerializer.Serialize(
                {|
                    scopeInput = TaskInputId.token SubmissionDeclarations.scopeInput
                    caseIdInput = TaskInputId.token SubmissionDeclarations.caseIdInput
                    referenceInput = TaskInputId.token SubmissionDeclarations.caseReferenceInput
                    caseScope = SubmissionDeclarations.caseScope
                    installationScope = SubmissionDeclarations.installationScope
                    rules =
                        SubmissionDeclarations.scopeRules
                        |> List.map (fun rule ->
                            {|
                                scope = rule.Scope
                                hasScope = rule.Scope.IsSome
                                hasId = rule.HasId
                                hasReference = rule.HasReference
                                kind = rule.Kind
                                input = rule.Input |> Option.map TaskInputId.token
                            |})
                |}
            )
            + " as const;"
            "export const reminderSelectionDefaults = "
            + JsonSerializer.Serialize(
                [ false; true ]
                |> List.map (fun hasView ->
                    let selection = ReminderSelection.defaults hasView

                    {|
                        hasView = hasView
                        state = ReminderStateSelection.token selection.State
                        includeInactive = selection.IncludeInactive
                    |})
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
