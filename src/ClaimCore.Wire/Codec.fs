namespace ClaimCore.Wire

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

/// One definition yields the decoder, the encoder and the schema, so they cannot diverge.
[<NoEquality; NoComparison>]
type Codec<'T> =
    {
        Read: string -> JsonElement -> Result<'T, Problem>
        Write: Utf8JsonWriter -> 'T -> unit
        Schema: Schema
        Definitions: Map<string, Schema>
    }

/// Accumulates the members of one closed object while its constructor is applied.
[<NoEquality; NoComparison>]
type Fields<'Whole, 'Ctor> =
    {
        ReadFields: string -> JsonElement -> Result<'Ctor, Problem>
        WriteFields: Utf8JsonWriter -> 'Whole -> unit
        Properties: (string * Schema) list
        FieldDefinitions: Map<string, Schema>
        OptionalMembers: string list
    }

/// One tagged-union case: its tag, how to read it, and how to write it when it matches.
[<NoEquality; NoComparison>]
type Variant<'U> =
    {
        Tag: string
        ReadVariant: string -> JsonElement -> Result<'U, Problem>
        TryWrite: Utf8JsonWriter -> 'U -> bool
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

    let escaped (value: string) : byte array =
        if not (Strict.isText value) then
            invalidArg
                (nameof value)
                "Wire strings require Unicode scalar text without NUL or noncharacters."

        let builder = StringBuilder(value.Length + 2)
        builder.Append('"') |> ignore

        for c in value do
            match c with
            | '"' -> builder.Append("\\\"") |> ignore
            | '\\' -> builder.Append("\\\\") |> ignore
            | '\b' -> builder.Append("\\b") |> ignore
            | '\t' -> builder.Append("\\t") |> ignore
            | '\n' -> builder.Append("\\n") |> ignore
            | '\f' -> builder.Append("\\f") |> ignore
            | '\r' -> builder.Append("\\r") |> ignore
            | c when int c < 0x20 ->
                builder.Append("\\u").Append((int c).ToString("x4", CultureInfo.InvariantCulture))
                |> ignore
            | c -> builder.Append(c) |> ignore

        builder.Append('"') |> ignore
        UTF8Encoding(false, true).GetBytes(builder.ToString())

    let writeString (writer: Utf8JsonWriter) (value: string) =
        writer.WriteRawValue(ReadOnlySpan(escaped value), skipInputValidation = true)

    let writeMember (writer: Utf8JsonWriter) (name: string) (value: string) =
        writer.WritePropertyName name
        writeString writer value

module Codec =
    let private merge (left: Map<string, Schema>) (right: Map<string, Schema>) =
        right
        |> Map.fold
            (fun state key value ->
                match Map.tryFind key state with
                | Some existing when existing <> value ->
                    invalidArg "codec" "Named codec definitions disagree."
                | _ -> Map.add key value state)
            left

    let private fail path reason = Error { Path = path; Reason = reason }

    let private ofKind
        kind
        (read: string -> JsonElement -> Result<'T, Problem>)
        path
        (element: JsonElement)
        =
        if element.ValueKind = kind then
            read path element
        else
            fail path ("expected " + string kind)

    let text: Codec<string> =
        {
            Read = ofKind JsonValueKind.String (fun _ element -> Ok(element.GetString() |> nonNull))
            Write = WriterProfile.writeString
            Schema = Schema.String(None, None, None, None)
            Definitions = Map.empty
        }

    let boundedText minimum maximum =
        if minimum < 0 || maximum < minimum then
            invalidArg (nameof minimum) "Use ordered text-length bounds."

        let within (value: string) =
            let length = value.EnumerateRunes() |> Seq.length
            length >= minimum && length <= maximum

        { text with
            Read =
                fun path element ->
                    text.Read path element
                    |> Result.bind (fun value ->
                        if within value then
                            Ok value
                        else
                            fail path "text outside its length bounds")
            Write =
                fun writer value ->
                    if not (within value) then
                        invalidArg (nameof value) "Text outside its wire bounds."

                    text.Write writer value
            Schema = Schema.String(None, None, Some minimum, Some maximum)
        }

    let uuid: Codec<Guid> =
        {
            Read =
                ofKind JsonValueKind.String (fun path element ->
                    let raw = element.GetString() |> nonNull

                    match Guid.TryParseExact(raw, "D") with
                    | true, value when value <> Guid.Empty && value.ToString "D" = raw -> Ok value
                    | _ -> fail path "expected a canonical nonzero UUID")
            Write =
                fun writer value ->
                    if value = Guid.Empty then
                        invalidArg (nameof value) "Use a nonzero UUID."

                    WriterProfile.writeString writer (value.ToString "D")
            Schema =
                Schema.String(
                    Some
                        "^(?!00000000-0000-0000-0000-000000000000$)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
                    Some "uuid",
                    Some 36,
                    Some 36
                )
            Definitions = Map.empty
        }

    let integer minimum maximum : Codec<int64> =
        if
            minimum < -Strict.MaximumSafeInteger
            || maximum > Strict.MaximumSafeInteger
            || minimum > maximum
        then
            invalidArg (nameof minimum) "Use ordered safe-integer bounds."

        {
            Read =
                ofKind JsonValueKind.Number (fun path element ->
                    match element.TryGetInt64() with
                    | true, value when value >= minimum && value <= maximum -> Ok value
                    | _ -> fail path "integer outside its range")
            Write =
                fun writer value ->
                    if value < minimum || value > maximum then
                        invalidArg (nameof value) "Integer outside its wire range."

                    writer.WriteNumberValue value
            Schema = Schema.Integer(minimum, maximum)
            Definitions = Map.empty
        }

    let boolean: Codec<bool> =
        {
            Read =
                fun path element ->
                    match element.ValueKind with
                    | JsonValueKind.True -> Ok true
                    | JsonValueKind.False -> Ok false
                    | _ -> fail path "expected a boolean"
            Write = fun writer value -> writer.WriteBooleanValue value
            Schema = Schema.Boolean
            Definitions = Map.empty
        }

    [<Literal>]
    let private InstantFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'"

    /// RFC 3339 UTC with exactly six fractional digits, matching ledger precision.
    let instant: Codec<DateTimeOffset> =
        {
            Read =
                ofKind JsonValueKind.String (fun path element ->
                    let raw = element.GetString() |> nonNull

                    match
                        DateTimeOffset.TryParseExact(
                            raw,
                            InstantFormat,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
                        )
                    with
                    | true, value when
                        value.ToString(InstantFormat, CultureInfo.InvariantCulture) = raw
                        ->
                        Ok value
                    | _ -> fail path "expected an RFC 3339 UTC instant with six fractional digits")
            Write =
                fun writer value ->
                    if value.Ticks % 10L <> 0L then
                        invalidArg (nameof value) "Use microsecond precision."

                    WriterProfile.writeString
                        writer
                        (value
                            .ToUniversalTime()
                            .ToString(InstantFormat, CultureInfo.InvariantCulture))
            Schema =
                Schema.String(
                    Some "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{6}Z$",
                    Some "date-time",
                    Some 27,
                    Some 27
                )
            Definitions = Map.empty
        }

    /// A closed set of string tokens mapped to values.
    let enumeration (cases: ('T * string) list) : Codec<'T> =
        {
            Read =
                ofKind JsonValueKind.String (fun path element ->
                    let raw = element.GetString() |> nonNull

                    match cases |> List.tryFind (snd >> (=) raw) with
                    | Some(value, _) -> Ok value
                    | None -> fail path "unknown token")
            Write =
                fun writer value ->
                    let _, token = cases |> List.find (fst >> (=) value)
                    WriterProfile.writeString writer token
            Schema = Schema.OneOf(cases |> List.map (snd >> Schema.Const), None)
            Definitions = Map.empty
        }

    let nullable (codec: Codec<'T>) : Codec<'T option> =
        {
            Read =
                fun path element ->
                    if element.ValueKind = JsonValueKind.Null then
                        Ok None
                    else
                        codec.Read path element |> Result.map Some
            Write =
                fun writer value ->
                    match value with
                    | Some item -> codec.Write writer item
                    | None -> writer.WriteNullValue()
            Schema = Schema.OneOf([ codec.Schema; Schema.Null ], None)
            Definitions = codec.Definitions
        }

    let list maxItems (codec: Codec<'T>) : Codec<'T list> =
        {
            Read =
                ofKind JsonValueKind.Array (fun path element ->
                    if element.GetArrayLength() > maxItems then
                        fail path "array exceeds its item limit"
                    else
                        element.EnumerateArray()
                        |> Seq.indexed
                        |> Seq.fold
                            (fun state (index, item) ->
                                state
                                |> Result.bind (fun values ->
                                    codec.Read (sprintf "%s[%d]" path index) item
                                    |> Result.map (fun value -> value :: values)))
                            (Ok [])
                        |> Result.map List.rev)
            Write =
                fun writer values ->
                    if values.Length > maxItems then
                        invalidArg (nameof values) "Array exceeds its wire limit."

                    writer.WriteStartArray()
                    values |> List.iter (codec.Write writer)
                    writer.WriteEndArray()
            Schema = Schema.Array(codec.Schema, 0, maxItems)
            Definitions = codec.Definitions
        }

    /// Registers the codec's schema once under a name and refers to it from users.
    let named name (codec: Codec<'T>) : Codec<'T> =
        { codec with
            Schema = Schema.Ref name
            Definitions = Map.add name codec.Schema codec.Definitions
        }

    let map read write (codec: Codec<'T>) : Codec<'U> =
        {
            Read = fun path element -> codec.Read path element |> Result.map read
            Write = fun writer value -> codec.Write writer (write value)
            Schema = codec.Schema
            Definitions = codec.Definitions
        }

    let constrained schema accepts (codec: Codec<'T>) =
        { codec with
            Read =
                fun path element ->
                    codec.Read path element
                    |> Result.bind (fun value ->
                        if accepts value then
                            Ok value
                        else
                            fail path "value outside its wire grammar")
            Write =
                fun writer value ->
                    if not (accepts value) then
                        invalidArg (nameof value) "Value outside its wire grammar."

                    codec.Write writer value
            Schema = schema
        }

    let boundedList minimum maximum codec =
        if minimum < 0 || maximum < minimum then
            invalidArg (nameof minimum) "Use ordered array bounds."

        list maximum codec
        |> constrained (Schema.Array(codec.Schema, minimum, maximum)) (fun values ->
            List.length values >= minimum)

    let constant token : Codec<unit> = enumeration [ (), token ]

    // ----- closed records -----------------------------------------------------------------

    let record (ctor: 'Ctor) : Fields<'Whole, 'Ctor> =
        {
            ReadFields = fun _ _ -> Ok ctor
            WriteFields = fun _ _ -> ()
            Properties = []
            FieldDefinitions = Map.empty
            OptionalMembers = []
        }

    let field
        (name: string)
        (get: 'Whole -> 'F)
        (codec: Codec<'F>)
        (fields: Fields<'Whole, 'F -> 'Rest>)
        : Fields<'Whole, 'Rest> =
        {
            ReadFields =
                fun path element ->
                    fields.ReadFields path element
                    |> Result.bind (fun apply ->
                        match element.TryGetProperty name with
                        | true, value -> codec.Read (path + "." + name) value |> Result.map apply
                        | _ -> fail path ("missing member " + name))
            WriteFields =
                fun writer whole ->
                    fields.WriteFields writer whole
                    writer.WritePropertyName name
                    codec.Write writer (get whole)
            Properties = fields.Properties @ [ name, codec.Schema ]
            FieldDefinitions = merge fields.FieldDefinitions codec.Definitions
            OptionalMembers = fields.OptionalMembers
        }

    let optionalField
        name
        (get: 'Whole -> 'F option)
        (codec: Codec<'F>)
        (fields: Fields<'Whole, 'F option -> 'Rest>)
        : Fields<'Whole, 'Rest> =
        {
            ReadFields =
                fun path element ->
                    fields.ReadFields path element
                    |> Result.bind (fun apply ->
                        match element.TryGetProperty(name: string) with
                        | false, _ -> Ok(apply None)
                        | true, value ->
                            codec.Read (path + "." + name) value |> Result.map (Some >> apply))
            WriteFields =
                fun writer whole ->
                    fields.WriteFields writer whole

                    match get whole with
                    | None -> ()
                    | Some value ->
                        writer.WritePropertyName name
                        codec.Write writer value
            Properties = fields.Properties @ [ name, codec.Schema ]
            FieldDefinitions = merge fields.FieldDefinitions codec.Definitions
            OptionalMembers = name :: fields.OptionalMembers
        }

    let private closed (members: string list) path (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            fail path "expected an object"
        else
            match
                element.EnumerateObject()
                |> Seq.tryFind (fun item -> not (List.contains item.Name members))
            with
            | Some _ -> fail path "unknown member"
            | None -> Ok()

    let build (fields: Fields<'Whole, 'Whole>) : Codec<'Whole> =
        let members = fields.Properties |> List.map fst

        {
            Read =
                fun path element ->
                    closed members path element
                    |> Result.bind (fun () -> fields.ReadFields path element)
            Write =
                fun writer value ->
                    writer.WriteStartObject()
                    fields.WriteFields writer value
                    writer.WriteEndObject()
            Schema = Schema.Object(fields.Properties, fields.OptionalMembers)
            Definitions = fields.FieldDefinitions
        }

    // ----- tagged unions ------------------------------------------------------------------

    /// A case whose payload members sit beside the tag member in one object.
    let variant
        tag
        (ctor: 'P -> 'U)
        (tryGet: 'U -> 'P option)
        (fields: Fields<'P, 'P>)
        tagMember
        : Variant<'U> =
        let members = tagMember :: (fields.Properties |> List.map fst)

        {
            Tag = tag
            ReadVariant =
                fun path element ->
                    closed members path element
                    |> Result.bind (fun () -> fields.ReadFields path element)
                    |> Result.map ctor
            TryWrite =
                fun writer value ->
                    match tryGet value with
                    | Some payload ->
                        writer.WriteStartObject()
                        WriterProfile.writeMember writer tagMember tag
                        fields.WriteFields writer payload
                        writer.WriteEndObject()
                        true
                    | None -> false
            VariantSchema =
                Schema.Object(
                    (tagMember, Schema.Const tag) :: fields.Properties,
                    fields.OptionalMembers
                )
            VariantDefinitions = fields.FieldDefinitions
        }

    /// A case that carries nothing but its tag.
    let unitVariant tag (value: 'U) tagMember : Variant<'U> =
        variant
            tag
            (fun () -> value)
            (fun candidate -> if candidate = value then Some() else None)
            (record ())
            tagMember

    let tagged
        (name: string)
        (tagMember: string)
        (variants: (string -> Variant<'U>) list)
        : Codec<'U> =
        let cases = variants |> List.map (fun make -> make tagMember)
        let caseName (case: Variant<'U>) = name + "." + case.Tag

        let definitions =
            cases
            |> List.fold
                (fun state case ->
                    Map.add
                        (caseName case)
                        case.VariantSchema
                        (merge state case.VariantDefinitions))
                Map.empty

        let union =
            Schema.OneOf(
                cases |> List.map (caseName >> Schema.Ref),
                Some(tagMember, cases |> List.map (fun case -> case.Tag, caseName case))
            )

        {
            Read =
                fun path element ->
                    if element.ValueKind <> JsonValueKind.Object then
                        fail path "expected an object"
                    else
                        match element.TryGetProperty tagMember with
                        | true, tag when tag.ValueKind = JsonValueKind.String ->
                            let value = tag.GetString() |> nonNull

                            match cases |> List.tryFind (fun case -> case.Tag = value) with
                            | Some case -> case.ReadVariant path element
                            | None -> fail (path + "." + tagMember) "unknown tag"
                        | _ -> fail path ("missing member " + tagMember)
            Write =
                fun writer value ->
                    if not (cases |> List.exists (fun case -> case.TryWrite writer value)) then
                        invalidOp "A value has no variant; the union codec is incomplete."
            Schema = Schema.Ref name
            Definitions = Map.add name union definitions
        }

    let nullValue: Codec<unit> =
        {
            Read = ofKind JsonValueKind.Null (fun _ _ -> Ok())
            Write = fun writer () -> writer.WriteNullValue()
            Schema = Schema.Null
            Definitions = Map.empty
        }

    let branch ctor tryGet (codec: Codec<'T>) =
        {
            Tag = ""
            ReadVariant = fun path element -> codec.Read path element |> Result.map ctor
            TryWrite =
                fun writer value ->
                    match tryGet value with
                    | None -> false
                    | Some payload ->
                        codec.Write writer payload
                        true
            VariantSchema = codec.Schema
            VariantDefinitions = codec.Definitions
        }

    let choice name (cases: Variant<'T> list) : Codec<'T> =
        let read path element =
            let values =
                cases
                |> List.choose (fun case ->
                    match case.ReadVariant path element with
                    | Ok value -> Some value
                    | Error _ -> None)

            match values with
            | [ value ] -> Ok value
            | _ -> fail path "expected exactly one variant"

        {
            Read = read
            Write =
                fun writer value ->
                    if not (cases |> List.exists (fun case -> case.TryWrite writer value)) then
                        invalidOp "A value has no variant; the union codec is incomplete."
            Schema = Schema.Ref name
            Definitions =
                cases
                |> List.fold (fun state case -> merge state case.VariantDefinitions) Map.empty
                |> Map.add name (Schema.OneOf(cases |> List.map _.VariantSchema, None))
        }

    let rec private literalTokens =
        function
        | Schema.Const token -> [ token ]
        | Schema.OneOf(variants, None) -> variants |> List.collect literalTokens
        | _ -> invalidArg "codec" "A discriminator requires closed string tokens."

    let private discriminatorMapping propertyName definitions =
        function
        | Schema.Ref name ->
            match Map.find name definitions with
            | Schema.Object(properties, _) ->
                properties
                |> List.find (fst >> (=) propertyName)
                |> snd
                |> literalTokens
                |> List.map (fun token -> token, name)
            | _ -> invalidArg "codec" "A discriminator branch must be a named object."
        | _ -> invalidArg "codec" "A discriminator branch must be named."

    /// Derives tag mappings from the same fixed or enumerated member used by each branch codec.
    let discriminated propertyName (codec: Codec<'T>) =
        match codec.Schema with
        | Schema.Ref name ->
            match Map.find name codec.Definitions with
            | Schema.OneOf(variants, None) ->
                let mapping =
                    variants |> List.collect (discriminatorMapping propertyName codec.Definitions)

                if (mapping |> List.map fst |> Set.ofList).Count <> mapping.Length then
                    invalidArg "codec" "Discriminator tokens must select one branch."

                { codec with
                    Definitions =
                        Map.add
                            name
                            (Schema.OneOf(variants, Some(propertyName, mapping)))
                            codec.Definitions
                }
            | _ -> invalidArg "codec" "A discriminator requires a named choice."
        | _ -> invalidArg "codec" "A discriminator requires a named choice."

    // ----- bytes --------------------------------------------------------------------------

    let encode (codec: Codec<'T>) (value: 'T) : byte array =
        use stream = new MemoryStream()

        use writer = new Utf8JsonWriter(stream, WriterProfile.options false)

        codec.Write writer value
        writer.Flush()
        stream.ToArray()

    let decode maximumBytes (codec: Codec<'T>) (bytes: byte array) : Result<'T, Problem> =
        Strict.parse maximumBytes bytes
        |> Result.bind (fun document ->
            use document = document
            codec.Read "$" document.RootElement)

    // ----- schema rendering ---------------------------------------------------------------

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
        | Schema.OneOf(variants, discriminator) -> unionSchema prefix writer variants discriminator
        | Schema.Ref name -> WriterProfile.writeMember writer "$ref" (prefix + name)

        writer.WriteEndObject()
