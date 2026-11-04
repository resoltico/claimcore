namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Protocol

open System.Text
open System.Text.Json
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


    let private entries =
        Catalog.document.Entries
        |> List.filter (fun entry ->
            List.contains (OperationCatalog.operation entry) CommandGrammar.operations)

    let private selected =
        function
        | CliDescription.Summary -> Some entries
        | CliDescription.Operation words ->
            entries
            |> List.tryFind (fun entry ->
                CommandGrammar.words (OperationCatalog.operation entry) = words)
            |> Option.map List.singleton

    let describe selector =
        selected selector
        |> Option.map (fun operations ->
            let values = operations |> List.map CatalogCodecs.entry.Write

            (if selector = CliDescription.Summary then
                 JsonValue.Array values
             else
                 List.head values)
            |> WireWriter.indentedBytes)

    let describeText language selector =
        selected selector
        |> Option.map (fun operations ->
            operations
            |> List.map (fun entry ->
                let operation = OperationCatalog.operation entry
                let inputs, _ = CommandGrammar.derive operation

                let message key =
                    MessageCatalogs.render language key Map.empty

                [
                    message (OperationCatalog.labelId operation)
                    message (OperationCatalog.meaningId operation)
                    CliIdentity.programName + " " + CommandGrammar.synopsis operation
                ]
                @ (inputs
                   |> List.filter (fun input -> input.Argument <> CatalogArgument.Implicit)
                   |> List.map (fun input ->
                       let allowed =
                           match input.Atom with
                           | CatalogAtom.Choices values
                           | CatalogAtom.Choice values ->
                               " ["
                               + (values |> List.map CommandGrammar.kebab |> String.concat "|")
                               + "]"
                           | _ -> ""

                       let guidance =
                           match input.Name with
                           | InteractionInput.Target(InputTarget.Field field) ->
                               ScalarGuidance.forField field
                           | (InteractionInput.Target InputTarget.Reason) ->
                               Some ScalarGuidance.forReason
                           | (InteractionInput.Target InputTarget.Text) ->
                               Some ScalarGuidance.forSearchText
                           | _ -> None

                       let hint =
                           guidance
                           |> Option.map (fun guidance ->
                               let args =
                                   guidance.Arguments
                                   |> Map.map (fun _ value ->
                                       match value with
                                       | GuidanceArgument.Count count ->
                                           count.ToString(
                                               System.Globalization.CultureInfo
                                                   .InvariantCulture
                                           )
                                       | GuidanceArgument.Code code -> code)

                               " " + MessageCatalogs.render language guidance.Key args)
                           |> Option.defaultValue ""

                       message input.Binding.LabelId
                       + ": "
                       + (input.Binding.MeaningId |> Option.map message |> Option.defaultValue "")
                       + allowed
                       + hint))
                @ (CommandGrammar.collections operation
                   |> List.map (fun definition ->
                       let binding = definition.Binding

                       message binding.LabelId
                       + ": "
                       + (binding.MeaningId |> Option.map message |> Option.defaultValue "")
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
            |> fun text -> Encoding.UTF8.GetBytes(text + "\n"))

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
            describeText language (CliDescription.Operation words)
            |> Option.map (fun bytes ->
                let globals =
                    CommandGrammar.globalOptions operation
                    |> List.map (fun (flag, value) ->
                        flag + (if value = "" then "" else " " + value))
                    |> fun options ->
                        options
                        @ (if operation.SelectedAct.IsSome then
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
                + MessageCatalogs.render language (OperationCatalog.labelId operation) Map.empty)
            |> String.concat "\n"
            |> fun text -> Some(text + "\n")

    let schema =
        function
        | CliSchema.Named name -> ContractDiscovery.schema name
        | CliSchema.Request endpoint -> ContractDiscovery.schema (endpoint + ".request")
        | CliSchema.Response endpoint -> ContractDiscovery.schema (endpoint + ".response")
