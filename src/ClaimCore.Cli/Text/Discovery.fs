namespace ClaimCore.Cli

open ClaimCore.CommandLine

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation

open System.Text
open ClaimCore.Json

module Discovery =
    let versionJson () =
        let identity = BuildIdentity.current

        let version =
            VersionFrame.codec.Write
                {
                    Product = identity.Product
                    Version = identity.Version
                    AssemblyVersion = identity.AssemblyVersion
                    FileVersion = identity.FileVersion
                }

        match version with
        | JsonValue.Object fields ->
            JsonValue.Object(
                fields @ [ "contractIdentity", JsonValue.String ContractIdentity.value ]
            )
            |> WireWriter.bytes
        | _ -> invalidOp "Version metadata requires an object."


    let private choices input =
        TaskChoiceId.ofInput input
        |> Option.map TaskChoiceId.tokens
        |> Option.defaultValue []

    let private partyExample (collection: CommandCollection) =
        let examples =
            collection.RowConstraints
            |> List.choose (function
                | TaskRowConstraint.AtLeastOne(members, _) -> List.tryHead members)
            |> Set.ofList

        collection.Members
        |> List.filter (fun value ->
            CommandGrammar.needsValue value || Set.contains value.Input examples)
        |> List.map (fun memberInput ->
            let value =
                match choices memberInput.Input, memberInput.Atom with
                | choice :: _, _ -> JsonValue.String choice
                | [], TaskAtom.Text -> JsonValue.String "Example"
                | [], TaskAtom.Integer -> JsonValue.Integer 1L
                | [], TaskAtom.Boolean -> JsonValue.Boolean true

            TaskInputId.token memberInput.Input, value)
        |> JsonValue.Object
        |> WireWriter.bytes
        |> Encoding.UTF8.GetString

    let private rowConstraintText rendering =
        function
        | TaskRowConstraint.AtLeastOne(_, missing) ->
            let code, parameters = DiagnosticCodecs.describeViolation missing
            ReplyTextFormat.diagnostic rendering code parameters

    let private rowConstraintJson =
        function
        | TaskRowConstraint.AtLeastOne(members, missing) ->
            let code, _ = DiagnosticCodecs.describeViolation missing

            JsonValue.Object
                [
                    "kind", JsonValue.String "AT_LEAST_ONE"
                    "members",
                    JsonValue.Array(members |> List.map (TaskInputId.token >> JsonValue.String))
                    "messageId", JsonValue.String(DiagnosticCode.key code)
                ]

    let private operationText (rendering: RenderingContext) operations =
        operations
        |> List.map (fun operation ->
            let inputs = CommandGrammar.inputs operation

            let defaults =
                (TaskDeclarations.describe operation).Inputs
                |> List.choose (fun input ->
                    input.DefaultText |> Option.map (fun value -> input.Input, value))
                |> Map.ofList

            let message key =
                MessageCatalogs.renderWithNumber
                    (ReplyTextFormat.number rendering)
                    rendering.Language
                    key
                    Map.empty

            let pairGuidance (input: CommandInput) =
                match input.Name with
                | TaskInputId.Target(InputTarget.Field field) ->
                    (BusinessField.describe field).Pairs
                    |> Option.bind (fun peer ->
                        inputs
                        |> List.tryFind (fun candidate ->
                            candidate.Name = TaskInputId.Target(InputTarget.Field peer)
                            && candidate.Argument <> CommandArgument.Implicit))
                    |> Option.map (fun peer ->
                        let key =
                            if
                                input.Declaration.Mode = TaskInputMode.Optional
                                && peer.Declaration.Mode = TaskInputMode.Optional
                            then
                                "label.moneyPairOptional"
                            else
                                "label.moneyPairTogether"

                        " " + message key)
                    |> Option.defaultValue ""
                | _ -> ""

            [
                message (TaskRouting.labelId operation)
                message (TaskRouting.meaningId operation)
                CliIdentity.programName + " " + CommandGrammar.synopsis operation
            ]
            @ (let execution = CommandGrammar.synopsis operation
               let draft = CommandGrammar.synopsisForDraft true operation

               // The dry-run form is shown only where its requirements differ, and says so.
               if (TaskRouting.act operation).IsSome && draft <> execution then
                   [
                       MessageCatalogs.renderCli
                           rendering.Language
                           (CliMessage.DryRunUsage(CliIdentity.programName + " " + draft))
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
                               ReplyTextFormat.number rendering count
                           | GuidanceArgument.Code code -> code)

                   let defaultValue =
                       Map.tryFind input.Name defaults
                       |> Option.map (fun value ->
                           let displayed =
                               match input.Atom with
                               | CommandAtom.Choice _ -> CommandGrammar.kebab value
                               | _ -> value

                           " "
                           + MessageCatalogs.renderCli
                               rendering.Language
                               (CliMessage.DefaultValue displayed))
                       |> Option.defaultValue ""

                   message input.LabelId
                   + ": "
                   + MessageCatalogs.render rendering.Language guidance.Key arguments
                   + allowed
                   + defaultValue
                   + pairGuidance input))
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
                              | GuidanceArgument.Count count ->
                                  ReplyTextFormat.number rendering count
                              | GuidanceArgument.Code code -> code)

                      MessageCatalogs.render rendering.Language guidance.Key arguments)
                   + " ["
                   + CommandGrammar.collectionFlag definition
                   + " "
                   + CommandGrammar.collectionPlaceholder definition
                   + "; "
                   + ReplyTextFormat.number rendering (int64 binding.MinimumItems)
                   + "–"
                   + ReplyTextFormat.number rendering (int64 binding.MaximumItems)
                   + "]"
                   + (if definition.Collection <> TaskCollectionId.Parties then
                          ""
                      else
                          let members =
                              definition.Members
                              |> List.map (fun item ->
                                  let token = TaskInputId.token item.Input

                                  let choices =
                                      match choices item.Input with
                                      | [] -> ""
                                      | values -> " " + String.concat "|" values

                                  let field = token + choices

                                  if CommandGrammar.needsValue item then
                                      field
                                  else
                                      "[" + field + "]")

                          "\n  "
                          + String.concat "\n  " members
                          + (definition.RowConstraints
                             |> List.map (fun constraint' ->
                                 "\n  " + rowConstraintText rendering constraint')
                             |> String.concat "")
                          + "\n  "
                          + CommandGrammar.collectionFlag definition
                          + " "
                          + CommandGrammar.quoteArgument (partyExample definition))))
            |> String.concat "\n")
        |> String.concat "\n\n"
        |> fun text -> Encoding.UTF8.GetBytes(text + "\n")

    let commandHelp (rendering: RenderingContext) (words: string list) =
        let matches =
            CommandGrammar.verbs
            |> List.filter (fun operation ->
                List.truncate words.Length (CommandGrammar.words operation) = words)

        match
            matches
            |> List.tryFind (fun operation -> CommandGrammar.words operation = words)
        with
        | Some operation ->
            Some(operationText rendering [ operation ])
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
                + MessageCatalogs.renderCli rendering.Language CliMessage.GlobalOptions
                + "\n  "
                + String.concat "\n  " globals
                + "\n")
        | None when matches.IsEmpty -> None
        | None ->
            matches
            |> List.map (fun operation ->
                String.concat " " (CommandGrammar.words operation)
                + " — "
                + MessageCatalogs.render
                    rendering.Language
                    (TaskRouting.labelId operation)
                    Map.empty)
            |> String.concat "\n"
            |> fun text -> Some(text + "\n")

    let helpJson (rendering: RenderingContext) topic text (operations: string list) =
        let input required (value: TaskInputDeclaration) =
            let choices = choices value.Input

            JsonValue.Object
                [
                    "id", JsonValue.String(TaskInputId.token value.Input)
                    "labelId",
                    JsonValue.String(TaskInputPresentation.messageKey value.Input "label")
                    "mode", JsonValue.String(TaskInputMode.token value.Mode)
                    "source", JsonValue.String(InputSource.token value.Source)
                    "choices", JsonValue.Array(choices |> List.map JsonValue.String)
                    "required", JsonValue.Boolean required
                    "default",
                    value.DefaultText
                    |> Option.map JsonValue.String
                    |> Option.defaultValue JsonValue.Null
                ]

        let operation kind =
            let declaration = TaskDeclarations.describe kind

            let required =
                CommandGrammar.inputs kind
                |> List.map (fun input -> input.Name, input.Required)
                |> Map.ofList

            let collections =
                CommandGrammar.collections kind
                |> List.map (fun item ->
                    JsonValue.Object
                        [
                            "id", JsonValue.String(TaskCollectionId.key item.Collection)
                            "minimumRows", JsonValue.Integer(int64 item.MinimumItems)
                            "maximumRows", JsonValue.Integer(int64 item.MaximumItems)
                            "flag", JsonValue.String(CommandGrammar.collectionFlag item)
                            "members",
                            item.Members
                            |> List.map (fun value ->
                                input (CommandGrammar.needsValue value) value)
                            |> JsonValue.Array
                            "rowConstraints",
                            JsonValue.Array(List.map rowConstraintJson item.RowConstraints)
                            if item.Collection = TaskCollectionId.Parties then
                                "example", JsonValue.String(partyExample item)
                        ])

            JsonValue.Object
                [
                    "command",
                    JsonValue.Array(CommandGrammar.words kind |> List.map JsonValue.String)
                    "synopsis", JsonValue.String(CommandGrammar.synopsis kind)
                    "task",
                    JsonValue.Object
                        [
                            "id", JsonValue.String(declaration.Id)
                            "inputs",
                            declaration.Inputs
                            |> List.map (fun value ->
                                input
                                    (Map.tryFind value.Input required |> Option.defaultValue false)
                                    value)
                            |> JsonValue.Array
                            "collections", JsonValue.Array collections
                        ]
                ]

        JsonValue.Object
            [
                "topic", JsonValue.String topic
                "language", JsonValue.String rendering.Language
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

    let commandHelpJson rendering words =
        commandHelp rendering words
        |> Option.map (fun text ->
            let matches =
                CommandGrammar.verbs
                |> List.filter (fun operation ->
                    List.truncate words.Length (CommandGrammar.words operation) = words)

            helpJson rendering (String.concat " " words) text matches)

    let schema =
        function
        | CliSchema.Named name -> ContractDiscovery.schema name
        | CliSchema.Request endpoint -> ContractDiscovery.schema (endpoint + ".request")
        | CliSchema.Response endpoint -> ContractDiscovery.schema (endpoint + ".response")
