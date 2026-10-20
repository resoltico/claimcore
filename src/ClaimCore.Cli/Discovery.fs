namespace ClaimCore.Cli

open ClaimCore.Wire

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

    let describe arguments =
        let descriptors = ContractDiscovery.descriptors ()

        match arguments with
        | []
        | [ "summary" ] -> Some descriptors
        | [ "fields" ] -> select descriptors "fields" "name" None
        | [ "fields"; name ] -> select descriptors "fields" "name" (Some name)
        | [ "commands" ] -> select descriptors "commands" "kind" None
        | [ "commands"; kind ] -> select descriptors "commands" "kind" (Some kind)
        | [ "authority" ] -> select descriptors "authorityCommands" "kind" None
        | [ "authority"; kind ] -> select descriptors "authorityCommands" "kind" (Some kind)
        | [ "endpoints" ] -> Some(ContractDiscovery.catalogue ())
        | [ "endpoints"; id ] -> select (ContractDiscovery.catalogue ()) "endpoints" "id" (Some id)
        | [ "diagnostics" ] -> Some(ContractDiscovery.presentation ())
        | _ -> None

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

    let describeText language arguments =
        describe arguments
        |> Option.map (fun bytes ->
            use document = JsonDocument.Parse(System.ReadOnlyMemory<byte> bytes)

            let rec render (value: JsonElement) =
                match value.ValueKind with
                | JsonValueKind.Array ->
                    value.EnumerateArray() |> Seq.map render |> String.concat "\n"
                | JsonValueKind.Object ->
                    value.EnumerateObject()
                    |> Seq.filter (fun item ->
                        List.contains
                            item.Name
                            [
                                "name"
                                "kind"
                                "id"
                                "label"
                                "meaning"
                                "inputs"
                                "fields"
                                "groups"
                                "type"
                                "required"
                                "path"
                                "method"
                            ])
                    |> Seq.map (fun item ->
                        let text = render item.Value

                        let text =
                            match value.TryGetProperty(item.Name + "Id") with
                            | true, identity ->
                                ContractDiscovery.localized
                                    language
                                    (identity.GetString() |> nonNull)
                                    text
                            | _ -> text

                        item.Name + ": " + text)
                    |> String.concat "\n"
                | JsonValueKind.String -> value.GetString() |> nonNull
                | _ -> value.GetRawText()

            let text =
                if List.isEmpty arguments || arguments = [ "summary" ] then
                    ContractDiscovery.helpTopic language "describe"
                elif arguments = [ "diagnostics" ] then
                    describeMessages language document.RootElement + "\n"
                else
                    render document.RootElement + "\nClaimCore.Cli schema invocation\n"

            Encoding.UTF8.GetBytes text)

    let schema arguments =
        match arguments with
        | [ name ] -> ContractDiscovery.schema name
        | [ endpoint; direction ] when direction = "request" || direction = "response" ->
            ContractDiscovery.schema (endpoint + "." + direction)
        | _ -> None
