namespace ClaimCore.Cli

open System.IO
open System.Reflection
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open ClaimCore.Wire

/// Offline discovery consumes the same tracked resources published for HTTPS clients.
module ContractDiscovery =
    let private resource name =
        use stream =
            Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaimCore." + name)
            |> nonNull

        use output = new MemoryStream()
        stream.CopyTo output
        output.ToArray()

    let descriptors () = resource "descriptors.json"
    let private openapi () = resource "openapi.json"

    let catalogue () =
        let endpoints =
            Endpoint.all
            |> List.map (fun endpoint ->
                {|
                    id = Endpoint.id endpoint
                    method = Endpoint.method endpoint
                    path = Endpoint.path endpoint
                    mutates = Endpoint.mutates endpoint
                |})

        JsonSerializer.SerializeToUtf8Bytes {| endpoints = endpoints |}

    let presentation () =
        use document = JsonDocument.Parse(System.ReadOnlyMemory<byte>(descriptors ()))

        document.RootElement.GetProperty("diagnostics").GetRawText()
        |> Encoding.UTF8.GetBytes

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
                .GetProperty(Endpoint.path endpoint)
                .GetProperty((Endpoint.method endpoint).ToLowerInvariant())

        let content =
            if direction = "request" then
                if endpoint = Endpoint.Session then
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
            let schema, definitions = Admission.schema Endpoint.Session
            Some(schemaDocument schema definitions)
        | "invocation" ->
            Some(schemaDocument Frames.invocationCodec.Schema Frames.invocationCodec.Definitions)
        | "response" -> Some(schemaDocument Frames.replyCodec.Schema Frames.replyCodec.Definitions)
        | "host-failure" ->
            Some(schemaDocument HostFailures.codec.Schema HostFailures.codec.Definitions)
        | _ ->
            let separator = name.LastIndexOf '.'

            let endpoint =
                if separator < 0 then
                    None
                else
                    Endpoint.tryParse (name.Substring(0, separator))

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
