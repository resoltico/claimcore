namespace ClaimCore.Cli

open System.IO
open System.Reflection
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Json

/// Offline discovery consumes the same tracked resources published for HTTPS clients.
module ContractDiscovery =
    let message identity =
        MessageCatalogs.render "en" identity Map.empty


    let localized language identity =
        MessageCatalogs.template language identity

    let entries () = OperationCatalog.entries

    let inputLabel (input: InteractionInput) =
        InteractionInput.messageKey input "label"

    let helpTopic language topic =
        MessageCatalogs.help language topic
        + (CliCommands.topicSynopses topic
           |> List.map (fun value -> CliIdentity.programName + " " + value + "\n")
           |> String.concat "")

    let private openapi () =
        WireWriter.indentedBytes ClaimCore.Protocol.OpenApi.document

    let catalogue () =
        let endpoints =
            Api.all
            |> List.map (fun endpoint ->
                {|
                    id = Api.id endpoint
                    method = (Api.method endpoint).Method
                    path = Api.path endpoint
                    effect = EndpointEffect.token (Api.effect endpoint)
                |})

        JsonSerializer.SerializeToUtf8Bytes {| endpoints = endpoints |}

    let presentation () =
        let keys = DiagnosticCode.all |> Map.values |> Seq.map _.MessageKey |> Set.ofSeq

        let templates = MessageCatalogs.templates "en"

        keys
        |> Seq.map (fun key ->
            let parts =
                Map.find key templates
                |> Template.parts
                |> List.map (function
                    | TemplatePart.Literal text -> Map.ofList [ "literal", text ]
                    | TemplatePart.Hole name -> Map.ofList [ "hole", name ])

            key, parts)
        |> Map.ofSeq
        |> JsonSerializer.SerializeToUtf8Bytes

    let private schemaDocument schema (definitions: Map<string, Schema>) =
        use output = new MemoryStream()
        use writer = new Utf8JsonWriter(output)
        Codec.writeSchema "#/components/schemas/" writer schema
        writer.Flush()
        let root = JsonNode.Parse(output.ToArray()) |> nonNull
        let components = JsonObject()
        let schemas = JsonObject()

        for KeyValue(name, value) in definitions do
            use stream = new MemoryStream()
            use itemWriter = new Utf8JsonWriter(stream)
            Codec.writeSchema "#/components/schemas/" itemWriter value
            itemWriter.Flush()
            schemas[name] <- JsonNode.Parse(stream.ToArray())

        components["schemas"] <- schemas
        root["components"] <- components
        root.ToJsonString() |> Encoding.UTF8.GetBytes

    let private endpointSchema endpoint direction (root: JsonElement) =
        let operation =
            root
                .GetProperty("paths")
                .GetProperty(Api.path endpoint)
                .GetProperty(((Api.method endpoint).Method).ToLowerInvariant())

        let content =
            if direction = "request" then
                if Api.id endpoint = "session" then
                    None
                else
                    Some(operation.GetProperty("requestBody").GetProperty("content"))
            else
                Some(operation.GetProperty("responses").GetProperty("200").GetProperty("content"))

        content
        |> Option.map (fun value -> value.GetProperty("application/json").GetProperty("schema"))

    let schema (name: string) =
        use document = JsonDocument.Parse(System.ReadOnlyMemory<byte>(openapi ()))
        let root = document.RootElement

        match name with
        | "openapi" -> Some(openapi ())
        | "session.request" ->
            Some(schemaDocument Api.session.Input.Schema Api.session.Input.Definitions)
        | "invocation" -> Some(schemaDocument CliSchemas.invocation CliSchemas.definitions)
        | "response" -> Some(schemaDocument CliSchemas.response CliSchemas.definitions)
        | "recovery" ->
            Some(schemaDocument CliSchemas.recoveryList.Schema CliSchemas.recoveryList.Definitions)
        | "host-failure" ->
            Some(schemaDocument HostFailureCodecs.reply.Schema HostFailureCodecs.reply.Definitions)
        | _ ->
            let separator = name.LastIndexOf '.'

            let endpoint =
                if separator < 0 then
                    None
                else
                    Api.tryFind (name.Substring(0, separator))

            let schema =
                match endpoint with
                | Some endpoint when name.EndsWith ".request" ->
                    endpointSchema endpoint "request" root
                | Some endpoint when name.EndsWith ".response" ->
                    endpointSchema endpoint "response" root
                | _ ->
                    match
                        root.GetProperty("components").GetProperty("schemas").TryGetProperty name
                    with
                    | true, value -> Some value
                    | _ -> None

            schema
            |> Option.map (fun item ->
                let value = JsonNode.Parse(item.GetRawText()) |> nonNull
                value["components"] <- JsonNode.Parse(root.GetProperty("components").GetRawText())
                value.ToJsonString() |> Encoding.UTF8.GetBytes)
