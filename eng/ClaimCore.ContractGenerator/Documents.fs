namespace ClaimCore.ContractGeneration

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open ClaimCore.Wire

module ContractDocuments =
    let private json (node: JsonNode) =
        node.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + "\n"
        |> System.Text.Encoding.UTF8.GetBytes

    let descriptors () =
        let root = JsonNode.Parse(ContractDescriptors.render ()) |> nonNull

        root["productVersion"] <-
            JsonValue.Create(ClaimCore.Application.BuildIdentity.current.Version)

        root["messages"] <- JsonNode.Parse(DefaultPresentation.render ())
        root["requestBindings"] <- JsonNode.Parse(RequestBindings.render ())
        json root

    let private schema (writer: Utf8JsonWriter) codec (definitions: Map<string, Schema>) =
        writer.WriteStartObject()
        WriterProfile.writeMember writer "$schema" "https://json-schema.org/draft/2020-12/schema"
        writer.WritePropertyName "oneOf"
        writer.WriteStartArray()

        for item in codec do
            Codec.writeSchema "#/components/schemas/" writer item

        writer.WriteEndArray()
        writer.WritePropertyName "components"
        writer.WriteStartObject()
        writer.WritePropertyName "schemas"
        writer.WriteStartObject()

        for KeyValue(name, item) in definitions do
            writer.WritePropertyName name
            Codec.writeSchema "#/components/schemas/" writer item

        writer.WriteEndObject()
        writer.WriteEndObject()
        writer.WriteEndObject()

    let frames () =
        let definitions =
            Frames.invocationCodec.Definitions
            |> Map.fold
                (fun state key value ->
                    match Map.tryFind key state with
                    | Some existing when existing <> value ->
                        invalidOp "Frame schema definitions disagree."
                    | _ -> Map.add key value state)
                Frames.replyCodec.Definitions

        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, WriterProfile.options true)
        schema writer [ Frames.invocationCodec.Schema; Frames.replyCodec.Schema ] definitions
        writer.Flush()
        Array.append (stream.ToArray()) [| byte '\n' |]
