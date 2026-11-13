namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation

open System.Text
open ClaimCore.Json

module Discovery =
    let versionJson () =
        let identity = BuildIdentity.current

        Codec.encode
            VersionFrame.codec
            {
                Product = identity.Product
                Version = identity.Version
                AssemblyVersion = identity.AssemblyVersion
                FileVersion = identity.FileVersion
            }


    let private operationText language operations =
        operations
        |> List.map (fun operation ->
            let inputs = CommandGrammar.inputs operation

            let message key =
                MessageCatalogs.render language key Map.empty

            [
                message (TaskRouting.labelId operation)
                message (TaskRouting.meaningId operation)
                CliIdentity.programName + " " + CommandGrammar.synopsis operation
            ]
            @ (if (TaskRouting.act operation).IsSome then
                   [
                       CliIdentity.programName
                       + " "
                       + CommandGrammar.synopsisForDraft true operation
                   ]
               else
                   [])
            @ (inputs
               |> List.filter (fun input -> input.Argument <> CommandArgument.Implicit)
               |> List.map (fun input ->
                   let allowed =
                       match input.Atom with
                       | CommandAtom.Choices values
                       | CommandAtom.Choice values ->
                           " ["
                           + (values |> List.map CommandGrammar.kebab |> String.concat "|")
                           + "]"
                       | _ -> ""

                   let guidance = ScalarGuidance.forInput input.Name

                   let arguments =
                       guidance.Arguments
                       |> Map.map (fun _ value ->
                           match value with
                           | GuidanceArgument.Count count ->
                               count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                           | GuidanceArgument.Code code -> code)

                   message input.LabelId
                   + ": "
                   + MessageCatalogs.render language guidance.Key arguments
                   + allowed))
            @ (CommandGrammar.collections operation
               |> List.map (fun definition ->
                   let binding = definition

                   message binding.LabelId
                   + ": "
                   + (let guidance = ScalarGuidance.forInput definition.Name

                      let arguments =
                          guidance.Arguments
                          |> Map.map (fun _ value ->
                              match value with
                              | GuidanceArgument.Count count -> string count
                              | GuidanceArgument.Code code -> code)

                      MessageCatalogs.render language guidance.Key arguments)
                   + " ["
                   + CommandGrammar.collectionFlag definition
                   + " "
                   + CommandGrammar.collectionPlaceholder definition
                   + "; "
                   + string binding.MinimumItems
                   + "–"
                   + string binding.MaximumItems
                   + "]"))
            |> String.concat "\n")
        |> String.concat "\n\n"
        |> fun text -> Encoding.UTF8.GetBytes(text + "\n")

    let commandHelp language (words: string list) =
        let matches =
            CommandGrammar.operations
            |> List.filter (fun operation ->
                List.truncate words.Length (CommandGrammar.words operation) = words)

        match
            matches
            |> List.tryFind (fun operation -> CommandGrammar.words operation = words)
        with
        | Some operation ->
            Some(operationText language [ operation ])
            |> Option.map (fun bytes ->
                let globals =
                    CommandGrammar.globalOptions operation
                    |> List.map (fun (flag, value) ->
                        flag + (if value = "" then "" else " " + value))
                    |> fun options ->
                        options
                        @ (if (TaskRouting.act operation).IsSome then
                               [ "--operation-id UUID" ]
                           else
                               [])

                Encoding.UTF8.GetString bytes
                + "\n"
                + MessageCatalogs.renderCli language CliMessage.GlobalOptions
                + "\n  "
                + String.concat "\n  " globals
                + "\n")
        | None when matches.IsEmpty -> None
        | None ->
            matches
            |> List.map (fun operation ->
                String.concat " " (CommandGrammar.words operation)
                + " — "
                + MessageCatalogs.render language (TaskRouting.labelId operation) Map.empty)
            |> String.concat "\n"
            |> fun text -> Some(text + "\n")

    let helpJson language topic text (operations: TaskId list) =
        let input (value: TaskInputDeclaration) =
            let choices =
                TaskChoiceId.ofInput value.Input
                |> Option.map TaskChoiceId.tokens
                |> Option.defaultValue []

            JsonValue.Object
                [
                    "id", JsonValue.String(TaskInputId.token value.Input)
                    "labelId",
                    JsonValue.String(TaskInputPresentation.messageKey value.Input "label")
                    "mode", JsonValue.String(TaskInputMode.token value.Mode)
                    "source", JsonValue.String(InputSource.token value.Source)
                    "choices", JsonValue.Array(choices |> List.map JsonValue.String)
                ]

        let operation kind =
            let declaration = TaskDeclarations.describe (kind)

            let collections =
                CommandGrammar.collections kind
                |> List.map (fun item ->
                    JsonValue.Object
                        [
                            "id", JsonValue.String(TaskCollectionId.key item.Collection)
                            "minimumRows", JsonValue.Integer(int64 item.MinimumItems)
                            "maximumRows", JsonValue.Integer(int64 item.MaximumItems)
                        ])

            JsonValue.Object
                [
                    "command",
                    JsonValue.Array(CommandGrammar.words kind |> List.map JsonValue.String)
                    "synopsis", JsonValue.String(CommandGrammar.synopsis kind)
                    "task",
                    JsonValue.Object
                        [
                            "id", JsonValue.String(TaskId.key declaration.Task)
                            "inputs", JsonValue.Array(declaration.Inputs |> List.map input)
                            "collections", JsonValue.Array collections
                        ]
                ]

        JsonValue.Object
            [
                "topic", JsonValue.String topic
                "language", JsonValue.String language
                "text", JsonValue.String text
                "operations", JsonValue.Array(List.map operation operations)
                "exitCodes",
                JsonValue.Array(
                    if topic = "exit-codes" then
                        CliIdentity.supportedExitCodes
                        |> List.map (fun code ->
                            JsonValue.Object
                                [
                                    "name", JsonValue.String(ExitCode.token code)
                                    "value", JsonValue.Integer(int64 (ExitCode.value code))
                                ])
                    else
                        []
                )
            ]
        |> WireWriter.indentedBytes

    let commandHelpJson language words =
        commandHelp language words
        |> Option.map (fun text ->
            let matches =
                CommandGrammar.operations
                |> List.filter (fun operation ->
                    List.truncate words.Length (CommandGrammar.words operation) = words)

            helpJson language (String.concat " " words) text matches)

    let schema =
        function
        | CliSchema.Named name -> ContractDiscovery.schema name
        | CliSchema.Request endpoint -> ContractDiscovery.schema (endpoint + ".request")
        | CliSchema.Response endpoint -> ContractDiscovery.schema (endpoint + ".response")
