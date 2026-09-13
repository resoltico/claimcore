namespace ClaimCore.Contracts

open System
open System.Buffers
open System.Text.Json

/// A deliberately small JSON Schema 2020-12 AST. Property order is semantic and preserved by renderers.
type Schema =
    internal
    | StringSchema of StringConstraints
    | IntegerSchema of IntegerConstraints
    | BooleanSchema
    | NullSchema
    | NeverSchema
    | ArraySchema of ArrayConstraints
    | TupleSchema of Schema list
    | ObjectSchema of ObjectConstraints
    | DictionarySchema of Schema
    | ConstantSchema of SchemaConstant
    | EnumerationSchema of SchemaConstant list
    | OneOfSchema of Schema list
    | ReferenceSchema of string

and StringConstraints =
    {
        Format: string option
        Pattern: string option
        MinimumLength: int option
        MaximumLength: int option
    }

and IntegerConstraints =
    {
        Minimum: int64 option
        Maximum: int64 option
    }

and ArrayConstraints =
    {
        Item: Schema
        MinimumItems: int option
        MaximumItems: int option
    }

and ObjectProperty =
    {
        Name: string
        Schema: Schema
        Required: bool
    }

and ObjectConstraints =
    {
        Properties: ObjectProperty list
        AdditionalProperties: bool
    }

and SchemaConstant =
    | TextConstant of string
    | IntegerConstant of int64
    | BooleanConstant of bool
    | NullConstant

/// A complete JSON Schema document with an explicit definition order.
type SchemaDocument =
    {
        Identifier: string
        Title: string
        Root: Schema
        Definitions: (string * Schema) list
    }

/// A canonical contract rendering is UTF-8 without BOM and has exactly one final LF.
type CanonicalContract =
    private
        {
            Text: string
            Bytes: byte array
        }

type CliWireContractFingerprint = private CliWireContractFingerprint of string

module CliWireContractFingerprint =
    let value (CliWireContractFingerprint value) = value

type WebWireContractFingerprint = private WebWireContractFingerprint of string

module WebWireContractFingerprint =
    let value (WebWireContractFingerprint value) = value

/// Constructors keep schema objects exact and ordered before any renderer sees them.
module Schema =
    let string format pattern minimumLength maximumLength =
        StringSchema
            {
                Format = format
                Pattern = pattern
                MinimumLength = minimumLength
                MaximumLength = maximumLength
            }

    let integer minimum maximum =
        IntegerSchema { Minimum = minimum; Maximum = maximum }

    let boolean = BooleanSchema
    let nullValue = NullSchema
    let never = NeverSchema

    let array item minimumItems maximumItems =
        ArraySchema
            {
                Item = item
                MinimumItems = minimumItems
                MaximumItems = maximumItems
            }

    let tuple items = TupleSchema items

    let property name schema required =
        if String.IsNullOrWhiteSpace(name) then
            invalidArg (nameof name) "Schema property names cannot be blank."

        {
            Name = name
            Schema = schema
            Required = required
        }

    let objectOf additionalProperties properties =
        let names = properties |> List.map _.Name

        if names |> Set.ofList |> Set.count <> names.Length then
            invalidArg (nameof properties) "Schema object properties must be unique."

        ObjectSchema
            {
                Properties = properties
                AdditionalProperties = additionalProperties
            }

    let dictionary value = DictionarySchema value

    let constant value = ConstantSchema value
    let enumeration values = EnumerationSchema values
    let oneOf schemas = OneOfSchema schemas
    let nullable schema = OneOfSchema [ schema; NullSchema ]

    let reference name =
        if String.IsNullOrWhiteSpace(name) then
            invalidArg (nameof name) "Schema references cannot be blank."

        ReferenceSchema name

    let private constantTypeScript value =
        match value with
        | TextConstant text -> JsonSerializer.Serialize(text)
        | IntegerConstant number -> number.ToString(Globalization.CultureInfo.InvariantCulture)
        | BooleanConstant true -> "true"
        | BooleanConstant false -> "false"
        | NullConstant -> "null"

    let private primitiveTypeScript schema =
        match schema with
        | StringSchema _ -> Some "string"
        | IntegerSchema _ -> Some "number"
        | BooleanSchema -> Some "boolean"
        | NullSchema -> Some "null"
        | NeverSchema -> Some "never"
        | ConstantSchema value -> Some(constantTypeScript value)
        | _ -> None

    let rec internal typeScript schema =
        match primitiveTypeScript schema with
        | Some value -> value
        | None -> compositeTypeScript schema

    and private compositeTypeScript schema =
        match schema with
        | EnumerationSchema values -> values |> List.map constantTypeScript |> String.concat " | "
        | OneOfSchema schemas -> schemas |> List.map typeScript |> String.concat " | "
        | ReferenceSchema name -> name
        | ArraySchema constraints -> "ReadonlyArray<" + typeScript constraints.Item + ">"
        | TupleSchema items ->
            "readonly [" + (items |> List.map typeScript |> String.concat ", ") + "]"
        | DictionarySchema value -> "Readonly<Record<string, " + typeScript value + ">>"
        | ObjectSchema constraints ->
            let properties =
                constraints.Properties
                |> List.map (fun property ->
                    let optional = if property.Required then "" else "?"

                    "readonly "
                    + JsonSerializer.Serialize(property.Name)
                    + optional
                    + ": "
                    + typeScript property.Schema
                    + ";")

            let indexer =
                if constraints.AdditionalProperties then
                    [ "readonly [key: string]: unknown;" ]
                else
                    []

            "{ " + String.concat " " (properties @ indexer) + " }"
        | _ -> invalidArg (nameof schema) "Expected a composite schema."

    let private sampleString constraints =
        match constraints.Format, constraints.Pattern with
        | Some "uuid", _ -> "10000000-0000-4000-8000-000000000001"
        | Some "date-time", _ -> "2026-01-01T00:00:00.0000000+00:00"
        | Some "date", _ -> "2026-01-01"
        | _, Some pattern when pattern.Contains("[0-9a-f]{64}") -> String.replicate 64 "0"
        | _, Some pattern when pattern.Contains("[A-Z]{3}") -> "EUR"
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
    let sampleBytes schema =
        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = false))
        writeSample writer schema
        writer.Flush()
        buffer.WrittenSpan.ToArray()

module CanonicalContract =
    let text contract = contract.Text
    let bytes contract = Array.copy contract.Bytes
