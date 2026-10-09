namespace ClaimCore.Contracts

open System
open System.Text.Json
open ClaimCore.Domain

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
        ScalarRule: ScalarRule option
        DiagnosticGuards: (string * InputViolation) list
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
                ScalarRule = None
                DiagnosticGuards = []
            }

    let internal scalarDiagnostics rule guards =
        function
        | StringSchema value ->
            StringSchema
                { value with
                    ScalarRule = Some rule
                    DiagnosticGuards = guards
                }
        | other -> other

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
        | ObjectSchema constraints when
            constraints.Properties.IsEmpty && not constraints.AdditionalProperties
            ->
            "Readonly<Record<string, never>>"
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

module CanonicalContract =
    let text contract = contract.Text
    let bytes contract = Array.copy contract.Bytes
