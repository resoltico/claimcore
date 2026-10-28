namespace ClaimCore.Json

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Encodings.Web
open System.Text.Json

module internal CodecSchema =
    let rec private objectSchema
        prefix
        (writer: Utf8JsonWriter)
        (properties: (string * Schema) list)
        optionalMembers
        =
        WriterProfile.writeMember writer "type" "object"
        writer.WriteBoolean("additionalProperties", false)
        writer.WritePropertyName "properties"
        writer.WriteStartObject()

        for name, value in properties do
            writer.WritePropertyName name
            writeSchema prefix writer value

        writer.WriteEndObject()
        writer.WritePropertyName "required"
        writer.WriteStartArray()

        for name, _ in properties do
            if not (List.contains name optionalMembers) then
                WriterProfile.writeString writer name

        writer.WriteEndArray()

    and private unionSchema prefix (writer: Utf8JsonWriter) variants discriminator =
        writer.WritePropertyName "oneOf"
        writer.WriteStartArray()
        variants |> List.iter (writeSchema prefix writer)
        writer.WriteEndArray()

        discriminator
        |> Option.iter (fun (propertyName, mapping) ->
            writer.WritePropertyName "discriminator"
            writer.WriteStartObject()
            WriterProfile.writeMember writer "propertyName" propertyName
            writer.WritePropertyName "mapping"
            writer.WriteStartObject()

            for tag, target in mapping do
                WriterProfile.writeMember writer tag (prefix + target)

            writer.WriteEndObject()
            writer.WriteEndObject())

    and writeSchema (prefix: string) (writer: Utf8JsonWriter) schema =
        writer.WriteStartObject()

        match schema with
        | Schema.Object(properties, optionalMembers) ->
            objectSchema prefix writer properties optionalMembers
        | Schema.Array(items, minimum, maximum) ->
            WriterProfile.writeMember writer "type" "array"
            writer.WritePropertyName "items"
            writeSchema prefix writer items
            writer.WriteNumber("minItems", minimum)
            writer.WriteNumber("maxItems", maximum)
        | Schema.String(pattern, format, minimum, maximum) ->
            WriterProfile.writeMember writer "type" "string"
            pattern |> Option.iter (WriterProfile.writeMember writer "pattern")
            format |> Option.iter (WriterProfile.writeMember writer "format")

            minimum
            |> Option.iter (fun value -> writer.WriteNumber("minLength", (value: int)))

            maximum
            |> Option.iter (fun value -> writer.WriteNumber("maxLength", (value: int)))
        | Schema.Integer(minimum, maximum) ->
            WriterProfile.writeMember writer "type" "integer"
            writer.WriteNumber("minimum", minimum)
            writer.WriteNumber("maximum", maximum)
        | Schema.Boolean -> WriterProfile.writeMember writer "type" "boolean"
        | Schema.Null -> WriterProfile.writeMember writer "type" "null"
        | Schema.Const value -> WriterProfile.writeMember writer "const" value
        | Schema.OneOf(variants, None) when Schema.tryLiteralSet variants |> Option.isSome ->
            writer.WritePropertyName "enum"
            writer.WriteStartArray()

            Schema.tryLiteralSet variants
            |> Option.get
            |> List.iter (WriterProfile.writeString writer)

            writer.WriteEndArray()
        | Schema.OneOf(variants, discriminator) -> unionSchema prefix writer variants discriminator
        | Schema.Ref name -> WriterProfile.writeMember writer "$ref" (prefix + name)

        writer.WriteEndObject()
