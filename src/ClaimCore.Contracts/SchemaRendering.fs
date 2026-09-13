namespace ClaimCore.Contracts

open System
open System.Buffers
open System.Text
open System.Text.Encodings.Web
open System.Text.Json

module internal CanonicalJson =
    let private writeConstant (writer: Utf8JsonWriter) constant =
        match constant with
        | TextConstant value -> writer.WriteStringValue(value)
        | IntegerConstant value -> writer.WriteNumberValue(value)
        | BooleanConstant value -> writer.WriteBooleanValue(value)
        | NullConstant -> writer.WriteNullValue()

    let private writeOptional (writer: Utf8JsonWriter) (name: string) value write =
        match value with
        | Some value ->
            writer.WritePropertyName(name)
            write value
        | None -> ()

    let private writeStringSchema (writer: Utf8JsonWriter) (constraints: StringConstraints) =
        writer.WriteStartObject()
        writer.WriteString("type", "string")
        writeOptional writer "format" constraints.Format writer.WriteStringValue
        writeOptional writer "pattern" constraints.Pattern writer.WriteStringValue
        writeOptional writer "minLength" constraints.MinimumLength writer.WriteNumberValue
        writeOptional writer "maxLength" constraints.MaximumLength writer.WriteNumberValue
        writer.WriteEndObject()

    let private writeIntegerSchema (writer: Utf8JsonWriter) (constraints: IntegerConstraints) =
        writer.WriteStartObject()
        writer.WriteString("type", "integer")
        writeOptional writer "minimum" constraints.Minimum writer.WriteNumberValue
        writeOptional writer "maximum" constraints.Maximum writer.WriteNumberValue
        writer.WriteEndObject()

    let private writeSimpleSchema (writer: Utf8JsonWriter) (name: string) (value: string) =
        writer.WriteStartObject()
        writer.WriteString(name, value)
        writer.WriteEndObject()

    let private writeArraySchema (writer: Utf8JsonWriter) (constraints: ArrayConstraints) write =
        writer.WriteStartObject()
        writer.WriteString("type", "array")
        writer.WritePropertyName("items")
        write constraints.Item
        writeOptional writer "minItems" constraints.MinimumItems writer.WriteNumberValue
        writeOptional writer "maxItems" constraints.MaximumItems writer.WriteNumberValue
        writer.WriteEndObject()

    let private writeTupleSchema (writer: Utf8JsonWriter) items write =
        writer.WriteStartObject()
        writer.WriteString("type", "array")

        match items with
        | [] ->
            writer.WriteBoolean("items", false)
            writer.WriteNumber("minItems", 0)
            writer.WriteNumber("maxItems", 0)
        | values ->
            writer.WritePropertyName("prefixItems")
            writer.WriteStartArray()
            values |> List.iter write
            writer.WriteEndArray()
            writer.WriteBoolean("items", false)
            writer.WriteNumber("minItems", values.Length)
            writer.WriteNumber("maxItems", values.Length)

        writer.WriteEndObject()

    let private writeObjectSchema (writer: Utf8JsonWriter) (constraints: ObjectConstraints) write =
        writer.WriteStartObject()
        writer.WriteString("type", "object")
        writer.WriteBoolean("additionalProperties", constraints.AdditionalProperties)
        writer.WritePropertyName("properties")
        writer.WriteStartObject()

        constraints.Properties
        |> List.iter (fun item ->
            writer.WritePropertyName(item.Name)
            write item.Schema)

        writer.WriteEndObject()
        let required = constraints.Properties |> List.filter _.Required |> List.map _.Name

        if not required.IsEmpty then
            writer.WritePropertyName("required")
            writer.WriteStartArray()
            required |> List.iter writer.WriteStringValue
            writer.WriteEndArray()

        writer.WriteEndObject()

    let private writeDictionarySchema (writer: Utf8JsonWriter) value write =
        writer.WriteStartObject()
        writer.WriteString("type", "object")
        writer.WritePropertyName("additionalProperties")
        write value
        writer.WriteEndObject()

    let private writeConstantSchema (writer: Utf8JsonWriter) constant =
        writer.WriteStartObject()
        writer.WritePropertyName("const")
        writeConstant writer constant
        writer.WriteEndObject()

    let private writeEnumerationSchema (writer: Utf8JsonWriter) constants =
        writer.WriteStartObject()
        writer.WritePropertyName("enum")
        writer.WriteStartArray()
        constants |> List.iter (writeConstant writer)
        writer.WriteEndArray()
        writer.WriteEndObject()

    let private writeOneOfSchema (writer: Utf8JsonWriter) schemas write =
        writer.WriteStartObject()
        writer.WritePropertyName("oneOf")
        writer.WriteStartArray()
        schemas |> List.iter write
        writer.WriteEndArray()
        writer.WriteEndObject()

    let private writeLeafSchema (writer: Utf8JsonWriter) schema =
        match schema with
        | StringSchema constraints ->
            writeStringSchema writer constraints
            true
        | IntegerSchema constraints ->
            writeIntegerSchema writer constraints
            true
        | BooleanSchema ->
            writeSimpleSchema writer "type" "boolean"
            true
        | NullSchema ->
            writeSimpleSchema writer "type" "null"
            true
        | NeverSchema ->
            writer.WriteBooleanValue(false)
            true
        | ConstantSchema constant ->
            writeConstantSchema writer constant
            true
        | EnumerationSchema constants ->
            writeEnumerationSchema writer constants
            true
        | _ -> false

    let rec private writeSchema (writer: Utf8JsonWriter) (schema: Schema) =
        if not (writeLeafSchema writer schema) then
            writeCompositeSchema writer schema

    and private writeCompositeSchema (writer: Utf8JsonWriter) schema =
        match schema with
        | ArraySchema constraints -> writeArraySchema writer constraints (writeSchema writer)
        | TupleSchema items -> writeTupleSchema writer items (writeSchema writer)
        | ObjectSchema constraints -> writeObjectSchema writer constraints (writeSchema writer)
        | DictionarySchema value -> writeDictionarySchema writer value (writeSchema writer)
        | OneOfSchema schemas -> writeOneOfSchema writer schemas (writeSchema writer)
        | ReferenceSchema name -> writeSimpleSchema writer "$ref" ("#/$defs/" + name)
        | _ -> invalidArg (nameof schema) "Expected a composite schema."

    let renderDocument (document: SchemaDocument) =
        let buffer = ArrayBufferWriter<byte>()

        let options =
            JsonWriterOptions(
                Indented = false,
                Encoder = JavaScriptEncoder.Default,
                SkipValidation = false
            )

        use writer = new Utf8JsonWriter(buffer, options)
        writer.WriteStartObject()
        writer.WriteString("$schema", "https://json-schema.org/draft/2020-12/schema")
        writer.WriteString("$id", document.Identifier)
        writer.WriteString("title", document.Title)
        writer.WritePropertyName("$defs")
        writer.WriteStartObject()

        document.Definitions
        |> List.iter (fun (name, schema) ->
            writer.WritePropertyName(name)
            writeSchema writer schema)

        writer.WriteEndObject()
        writer.WriteString("$ref", "#/$defs/root")
        writer.WriteEndObject()
        writer.Flush()
        let bytes = Array.append (buffer.WrittenSpan.ToArray()) [| byte '\n' |]

        {
            Text = Encoding.UTF8.GetString(bytes)
            Bytes = bytes
        }

    let renderSchema (document: SchemaDocument) (schema: Schema) =
        renderDocument
            { document with
                Root = schema
                Definitions = ("root", schema) :: document.Definitions
            }
