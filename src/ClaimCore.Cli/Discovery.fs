namespace ClaimCore.Cli

open System.Text
open System.Text.Json
open ClaimCore.Contract
open ClaimCore.Json

module Discovery =
    let versionJson () =
        let identity = BuildIdentity.current

        JsonSerializer.SerializeToUtf8Bytes
            {|
                product = identity.Product
                version = identity.Version
                assemblyVersion = identity.AssemblyVersion
                fileVersion = identity.FileVersion
            |}

    let private selected =
        function
        | CliDescription.Summary -> Some Catalog.document.Entries
        | CliDescription.Operation words ->
            Catalog.document.Entries
            |> List.tryFind (fun entry ->
                OperationCatalog.words (OperationCatalog.operation entry) = words)
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
                let inputs, _ = OperationCatalog.derive operation

                let message key =
                    MessageCatalogs.render language key Map.empty

                [
                    message (OperationCatalog.labelId operation)
                    message (OperationCatalog.meaningId operation)
                    CliIdentity.programName + " " + OperationCatalog.synopsis operation
                ]
                @ (inputs
                   |> List.filter (fun input -> input.Argument <> CatalogArgument.Implicit)
                   |> List.map (fun input ->
                       let allowed =
                           match input.Atom with
                           | CatalogAtom.Choice values ->
                               " ["
                               + (values |> List.map OperationCatalog.kebab |> String.concat "|")
                               + "]"
                           | _ -> ""

                       message input.Binding.LabelId
                       + ": "
                       + (input.Binding.MeaningId |> Option.map message |> Option.defaultValue "")
                       + allowed))
                |> String.concat "\n")
            |> String.concat "\n\n"
            |> fun text -> Encoding.UTF8.GetBytes(text + "\n"))

    let schema =
        function
        | CliSchema.Named name -> ContractDiscovery.schema name
        | CliSchema.Request endpoint -> ContractDiscovery.schema (endpoint + ".request")
        | CliSchema.Response endpoint -> ContractDiscovery.schema (endpoint + ".response")
