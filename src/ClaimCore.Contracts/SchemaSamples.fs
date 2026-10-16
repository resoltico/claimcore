namespace ClaimCore.Contracts

open ClaimCore.ClientProtocol

open System
open System.Buffers
open System.Text.Json

module SchemaSamples =
    let private sampleString constraints =
        match constraints.Format, constraints.Pattern with
        | Some "uuid", _ -> "10000000-0000-4000-8000-000000000001"
        | Some "date-time", _ -> "2026-01-01T00:00:00.0000000+00:00"
        | Some "date", _ -> "2026-01-01"
        | Some "uri", _ -> "https://identity.example.test/"
        | _, Some pattern when pattern.Contains("[0-9a-f]{64}") -> String.replicate 64 "0"
        | _, Some pattern when pattern.Contains("[A-Z]{2}") -> "LV"
        | _, Some pattern when pattern.Contains("[A-Z]{3}") -> "EUR"
        | _, Some pattern when pattern.Contains("(?!0$)") -> "1"
        | _, Some pattern when pattern.Contains("[1-9][0-9]{0,") -> "0"
        | _ -> String.replicate (defaultArg constraints.MinimumLength 1 |> max 1) "x"

    let private writeConstantSample (writer: Utf8JsonWriter) value =
        match value with
        | TextConstant text -> writer.WriteStringValue(text)
        | IntegerConstant number -> writer.WriteNumberValue(number)
        | BooleanConstant boolean -> writer.WriteBooleanValue(boolean)
        | NullConstant -> writer.WriteNullValue()

    let private writePrimitiveSample (writer: Utf8JsonWriter) schema =
        match schema with
        | StringSchema constraints ->
            writer.WriteStringValue(sampleString constraints)
            true
        | IntegerSchema constraints ->
            writer.WriteNumberValue(defaultArg constraints.Minimum 0L)
            true
        | BooleanSchema ->
            writer.WriteBooleanValue(false)
            true
        | NullSchema ->
            writer.WriteNullValue()
            true
        | NeverSchema -> invalidOp "A never schema has no valid sample."
        | ConstantSchema value ->
            writeConstantSample writer value
            true
        | _ -> false

    let rec private writeSample (writer: Utf8JsonWriter) schema =
        if not (writePrimitiveSample writer schema) then
            writeCompositeSample writer schema

    and private writeCompositeSample (writer: Utf8JsonWriter) schema =
        match schema with
        | EnumerationSchema(first :: _) -> writeSample writer (ConstantSchema first)
        | EnumerationSchema [] -> invalidOp "Cannot sample an empty schema enumeration."
        | OneOfSchema(first :: _) -> writeSample writer first
        | OneOfSchema [] -> invalidOp "Cannot sample an empty one-of schema."
        | ReferenceSchema _ -> invalidOp "A standalone schema sample cannot resolve a reference."
        | DictionarySchema _ ->
            writer.WriteStartObject()
            writer.WriteEndObject()
        | ArraySchema constraints ->
            writer.WriteStartArray()

            for _ in 1 .. defaultArg constraints.MinimumItems 0 do
                writeSample writer constraints.Item

            writer.WriteEndArray()
        | TupleSchema items ->
            writer.WriteStartArray()
            items |> List.iter (writeSample writer)
            writer.WriteEndArray()
        | ObjectSchema constraints ->
            writer.WriteStartObject()

            constraints.Properties
            |> List.filter _.Required
            |> List.iter (fun property ->
                writer.WritePropertyName(property.Name)
                writeSample writer property.Schema)

            writer.WriteEndObject()
        | _ -> invalidArg (nameof schema) "Expected a composite schema."

    /// Deterministic synthetic exemplar used only by contract corpora and conformance tests.
    let bytes schema =
        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = false))
        writeSample writer schema
        writer.Flush()
        buffer.WrittenSpan.ToArray()

    let rec private resolve definitions depth schema =
        if depth > 32 then
            invalidArg "document" "Schema references exceed the supported depth."

        let next = resolve definitions (depth + 1)

        match schema with
        | ReferenceSchema name -> definitions |> Map.find name |> next
        | OneOfSchema items -> OneOfSchema(List.map next items)
        | ArraySchema value -> ArraySchema { value with Item = next value.Item }
        | TupleSchema items -> TupleSchema(List.map next items)
        | DictionarySchema value -> DictionarySchema(next value)
        | ObjectSchema value ->
            ObjectSchema
                { value with
                    Properties =
                        value.Properties
                        |> List.map (fun item -> { item with Schema = next item.Schema })
                }
        | value -> value

    let document (document: SchemaDocument) =
        resolve (Map.ofList document.Definitions) 0 document.Root |> bytes

    /// Each top-level command or outcome alternative has a qualifying exemplar.
    let variants (document: SchemaDocument) =
        let root = resolve (Map.ofList document.Definitions) 0 document.Root

        let alternatives =
            match root with
            | OneOfSchema items -> items
            | ObjectSchema value ->
                match
                    value.Properties |> List.tryFind (fun p -> p.Name = "outcome" || p.Name = "act")
                with
                | Some property ->
                    match property.Schema with
                    | OneOfSchema items ->
                        items
                        |> List.map (fun schema ->
                            ObjectSchema
                                { value with
                                    Properties =
                                        value.Properties
                                        |> List.map (fun p ->
                                            if p.Name = property.Name then
                                                { p with Schema = schema }
                                            else
                                                p)
                                })
                    | _ -> [ root ]
                | None -> [ root ]
            | _ -> [ root ]

        alternatives |> List.map bytes
