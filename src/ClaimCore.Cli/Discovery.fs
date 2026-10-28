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


    let private selected =
        function
        | CliDescription.Summary -> Some Catalog.document.Entries
        | CliDescription.Operation words ->
            Catalog.document.Entries
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
                |> String.concat "\n")
            |> String.concat "\n\n"
            |> fun text -> Encoding.UTF8.GetBytes(text + "\n"))

    let schema =
        function
        | CliSchema.Named name -> ContractDiscovery.schema name
        | CliSchema.Request endpoint -> ContractDiscovery.schema (endpoint + ".request")
        | CliSchema.Response endpoint -> ContractDiscovery.schema (endpoint + ".response")
