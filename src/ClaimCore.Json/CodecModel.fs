namespace ClaimCore.Json

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Encodings.Web
open System.Text.Json

/// The closed schema vocabulary the wire uses. It is derived from codecs, never written by hand.
[<RequireQualifiedAccess>]
type Schema =
    | Object of properties: (string * Schema) list * optionalMembers: string list
    | Array of items: Schema * minItems: int * maxItems: int
    | String of
        pattern: string option *
        format: string option *
        minimumLength: int option *
        maximumLength: int option
    | Integer of minimum: int64 * maximum: int64
    | Boolean
    | Null
    | Const of string
    | OneOf of variants: Schema list * discriminator: (string * (string * string) list) option
    | Ref of string
    | Vocabulary of name: string * shape: Schema

module Schema =
    /// Distinct string literals are disjoint, so their oneOf is exactly one enum.
    let tryLiteralSet variants =
        let values =
            variants
            |> List.choose (function
                | Schema.Const value -> Some value
                | _ -> None)

        if
            values.Length > 1
            && values.Length = List.length variants
            && List.distinct values = values
        then
            Some values
        else
            None

/// Named schema meaning is shared by codecs, OpenAPI and CLI framing.
module SchemaDefinitions =
    let merge (left: Map<string, Schema>) (right: Map<string, Schema>) =
        Map.fold
            (fun definitions key value ->
                match Map.tryFind key definitions with
                | Some existing when existing <> value ->
                    invalidArg (nameof right) "Named schema definitions disagree."
                | _ -> Map.add key value definitions)
            left
            right

/// One definition yields the decoder, the encoder and the schema, so they cannot diverge.
[<NoEquality; NoComparison>]
type Codec<'T> =
    {
        Read: string -> JsonValue -> Result<'T, Problem>
        Write: 'T -> JsonValue
        Schema: Schema
        Definitions: Map<string, Schema>
    }

/// Accumulates the members of one closed object while its constructor is applied.
[<NoEquality; NoComparison>]
type Fields<'Whole, 'Ctor> =
    {
        ReadFields: string -> JsonValue -> Result<'Ctor, Problem>
        WriteFields: 'Whole -> (string * JsonValue) list
        Properties: (string * Schema) list
        PropertyDefinitions: Map<string, Schema>
        OptionalMembers: string list
    }

/// One union alternative. Tagged variants describe payload fields; taggedOn owns their envelope.
[<NoEquality; NoComparison>]
type Variant<'U> =
    {
        Tag: string
        ReadVariant: string -> JsonValue -> Result<'U, Problem>
        TryWrite: 'U -> JsonValue option
        VariantSchema: Schema
        VariantDefinitions: Map<string, Schema>
    }

/// The writer profile: string values escape only quotation mark, reverse solidus and C0 controls;
/// every other scalar is written as raw UTF-8. Member names are ASCII constants.
module WriterProfile =
    let options indented =
        JsonWriterOptions(
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = indented
        )

    let escaped value =
        WireWriter.bytes (JsonValue.String value)

    let writeString (writer: Utf8JsonWriter) (value: string) =
        writer.WriteRawValue(ReadOnlySpan(escaped value), skipInputValidation = true)

    let writeMember (writer: Utf8JsonWriter) (name: string) (value: string) =
        writer.WritePropertyName name
        writeString writer value
