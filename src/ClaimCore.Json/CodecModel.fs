namespace ClaimCore.Json

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Encodings.Web
open System.Text.Json

/// The finite schema vocabulary used by codec declarations and generated transport contracts.
[<RequireQualifiedAccess>]
type Schema =
    | Object of properties: (string * Schema) list * optionalMembers: string list
    | Array of items: Schema * minItems: int * maxItems: int
    | String of
        pattern: string option *
        format: string option *
        minimumLength: int option *
        maximumLength: int option *
        maximumUtf8Bytes: int option
    | Integer of minimum: int64 * maximum: int64
    | Boolean
    | Null
    | Const of string
    | OneOf of variants: Schema list * discriminator: (string * (string * string) list) option
    | Ref of string
    | Vocabulary of name: string * shape: Schema
    /// A closed enum is partitioned between a scalar array and a keyed-record array.
    | EnumPartition of
        shape: Schema *
        values: string list *
        selected: string *
        rejected: string *
        rejectedKey: string

module Schema =
    [<Literal>]
    let MaximumUtf8BytesKeyword = "x-claimcore-maxUtf8Bytes"

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

/// Validate admits structural JSON; Read also interprets it into a typed value and may refuse semantics.
/// Schema describes structure, not arbitrary interpretation predicates.
[<NoEquality; NoComparison>]
type Codec<'T> =
    internal
        {
            Reader: string -> JsonValue -> Result<'T, Problem>
            Validation: string -> JsonValue -> Result<unit, Problem>
            Writer: 'T -> JsonValue
            Shape: Schema
            NamedSchemas: Map<string, Schema>
        }

    member this.Read = this.Reader
    member this.Validate = this.Validation
    member this.Write = this.Writer
    member this.Schema = this.Shape
    member this.Definitions = this.NamedSchemas

/// Accumulates the members of one closed object while its constructor is applied.
[<NoEquality; NoComparison>]
type Fields<'Whole, 'Ctor> =
    internal
        {
            ReadMembers: string -> JsonValue -> Result<'Ctor, Problem>
            Validation: string -> JsonValue -> Result<unit, Problem>
            WriteMembers: 'Whole -> (string * JsonValue) list
            MemberShapes: (string * Schema) list
            MemberDefinitions: Map<string, Schema>
            OptionalNames: string list
        }

    member this.ReadFields = this.ReadMembers
    member this.Validate = this.Validation
    member this.WriteFields = this.WriteMembers
    member this.Properties = this.MemberShapes
    member this.PropertyDefinitions = this.MemberDefinitions
    member this.OptionalMembers = this.OptionalNames

/// One union alternative. Tagged variants describe payload fields; taggedOn owns their envelope.
[<NoEquality; NoComparison>]
type Variant<'U> =
    internal
        {
            Discriminator: string
            SchemaName: string option
            ReadCase: string -> JsonValue -> Result<'U, Problem>
            Validation: string -> JsonValue -> Result<unit, Problem>
            WriteCase: 'U -> JsonValue option
            CaseShape: Schema
            CaseDefinitions: Map<string, Schema>
        }

    member this.Tag = this.Discriminator
    member this.ReadVariant = this.ReadCase
    member this.Validate = this.Validation
    member this.TryWrite = this.WriteCase
    member this.VariantSchema = this.CaseShape
    member this.VariantDefinitions = this.CaseDefinitions

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
