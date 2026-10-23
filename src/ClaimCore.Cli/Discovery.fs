namespace ClaimCore.Cli

open ClaimCore.Contract

open ClaimCore.Contract.Protocol

open System.Text
open System.Text.Json

module Discovery =
    let versionJson () =
        let identity = BuildIdentity.current

        JsonSerializer.SerializeToUtf8Bytes(
            {|
                product = identity.Product
                version = identity.Version
                assemblyVersion = identity.AssemblyVersion
                fileVersion = identity.FileVersion
            |}
        )

    let private select
        (bytes: byte array)
        (collection: string)
        (key: string)
        (requested: string option)
        =
        use document = JsonDocument.Parse(System.ReadOnlyMemory<byte> bytes)
        let entries = document.RootElement.GetProperty(collection)

        match requested with
        | None -> Some(Encoding.UTF8.GetBytes(entries.GetRawText()))
        | Some value ->
            entries.EnumerateArray()
            |> Seq.tryFind (fun item ->
                item.GetProperty(key).GetString() |> Option.ofObj = Some value)
            |> Option.map (fun item -> Encoding.UTF8.GetBytes(item.GetRawText()))

    let describe selection =
        let descriptors = ContractDiscovery.descriptors ()

        match selection with
        | CliDescription.Summary -> Some descriptors
        | CliDescription.Fields name -> select descriptors "fields" "name" name
        | CliDescription.Commands kind -> select descriptors "commands" "kind" kind
        | CliDescription.Authority kind -> select descriptors "authorityCommands" "kind" kind
        | CliDescription.Endpoints None -> Some(ContractDiscovery.catalogue ())
        | CliDescription.Endpoints(Some endpoint) ->
            select (ContractDiscovery.catalogue ()) "endpoints" "id" (Some(Endpoint.id endpoint))
        | CliDescription.Diagnostics -> Some(ContractDiscovery.presentation ())

    let private describeMessages language (value: JsonElement) =
        value.EnumerateObject()
        |> Seq.filter (fun item ->
            item.Name.StartsWith("diagnostic.", System.StringComparison.Ordinal))
        |> Seq.map (fun item ->
            let template =
                item.Value.EnumerateArray()
                |> Seq.map (fun part ->
                    match part.TryGetProperty "literal" with
                    | true, text -> text.GetString() |> nonNull
                    | _ -> "{" + (part.GetProperty("hole").GetString() |> nonNull) + "}")
                |> String.concat ""

            item.Name + ": " + ContractDiscovery.localized language item.Name template)
        |> String.concat "\n"

    let private summary language =
        [
            CliDescription.Fields None
            CliDescription.Commands None
            CliDescription.Authority None
        ]
        |> List.collect (fun selection ->
            match describe selection with
            | None -> []
            | Some bytes ->
                use document = JsonDocument.Parse(System.ReadOnlyMemory<byte> bytes)

                document.RootElement.EnumerateArray()
                |> Seq.map (fun item ->
                    let name =
                        match item.TryGetProperty "name" with
                        | true, value -> value.GetString() |> nonNull
                        | _ -> item.GetProperty("kind").GetString() |> nonNull

                    let label =
                        ContractDiscovery.localized
                            language
                            (item.GetProperty("labelId").GetString() |> nonNull)
                            (item.GetProperty("label").GetString() |> nonNull)

                    name + " — " + label)
                |> Seq.toList)
        |> String.concat "\n"

    let describeText language selection =
        describe selection
        |> Option.map (fun bytes ->
            use document = JsonDocument.Parse(System.ReadOnlyMemory<byte> bytes)

            use definitions =
                JsonDocument.Parse(System.ReadOnlyMemory<byte>(ContractDiscovery.descriptors ()))

            let routes =
                match selection with
                | CliDescription.Endpoints endpoint ->
                    (endpoint |> Option.map List.singleton |> Option.defaultValue Endpoint.all)
                    |> List.collect (fun endpoint ->
                        [
                            "ClaimCore.Cli schema " + Endpoint.id endpoint + " request"
                            "ClaimCore.Cli schema " + Endpoint.id endpoint + " response"
                        ])
                    |> String.concat "\n"
                | _ -> "ClaimCore.Cli schema invocation"

            let target =
                match selection with
                | CliDescription.Authority _ ->
                    DescriptorText.render
                        language
                        definitions.RootElement
                        (definitions.RootElement.GetProperty "authorityTarget")
                    + "\n"
                | _ -> ""

            let text =
                if selection = CliDescription.Summary then
                    ContractDiscovery.helpTopic language "describe"
                    + summary language
                    + "\nClaimCore.Cli schema invocation\n"
                elif selection = CliDescription.Diagnostics then
                    describeMessages language document.RootElement + "\n"
                else
                    target
                    + DescriptorText.render language definitions.RootElement document.RootElement
                    + "\n"
                    + routes
                    + "\n"

            Encoding.UTF8.GetBytes text)

    let schema =
        function
        | CliSchema.Named name -> ContractDiscovery.schema name
        | CliSchema.Request endpoint -> ContractDiscovery.schema (Endpoint.id endpoint + ".request")
        | CliSchema.Response endpoint ->
            ContractDiscovery.schema (Endpoint.id endpoint + ".response")
