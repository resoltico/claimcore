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

module Codec =
    let private fail path reason = Error { Path = path; Reason = reason }

    let text: Codec<string> =
        {
            Read =
                fun path value ->
                    match value with
                    | JsonValue.String text when
                        JsonValue.isText text && not (text.Contains '\u0000')
                        ->
                        Ok text
                    | _ -> fail path "expected Unicode scalar text without NUL or noncharacters"
            Write =
                fun value ->
                    if not (JsonValue.isText value) || value.Contains '\u0000' then
                        invalidArg
                            (nameof value)
                            "Expected Unicode scalar text without NUL or noncharacters."

                    JsonValue.String value
            Schema = Schema.String(None, None, None, None)
            Definitions = Map.empty
        }

    let private orderedBounds lower upper minimum maximum message =
        if minimum < lower || maximum > upper || minimum > maximum then
            invalidArg (nameof minimum) message

    let boundedText minimum maximum =
        orderedBounds 0 Int32.MaxValue minimum maximum "Use ordered text-length bounds."

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
                fun value ->
                    if not (within value) then
                        invalidArg (nameof value) "Text outside its wire bounds."

                    text.Write value
            Schema = Schema.String(None, None, Some minimum, Some maximum)
        }

    /// Registers the codec's schema once under a name and refers to it from users.
    let named name (codec: Codec<'T>) : Codec<'T> =
        { codec with
            Schema = Schema.Ref name
            Definitions = Map.add name codec.Schema codec.Definitions
        }

    let uuid: Codec<Guid> =
        {
            Read =
                fun path element ->
                    text.Read path element
                    |> Result.bind (fun raw ->

                        match Guid.TryParseExact(raw, "D") with
                        | true, value when value <> Guid.Empty && value.ToString "D" = raw ->
                            Ok value
                        | _ -> fail path "expected a canonical nonzero UUID")
            Write =
                fun value ->
                    if value = Guid.Empty then
                        invalidArg (nameof value) "Use a nonzero UUID."

                    JsonValue.String(value.ToString "D")
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
        |> named "Uuid"

    let integer minimum maximum : Codec<int64> =
        orderedBounds
            (-JsonValue.MaximumInteger)
            JsonValue.MaximumInteger
            minimum
            maximum
            "Use ordered safe-integer bounds."

        {
            Read =
                fun path element ->
                    match element with
                    | JsonValue.Integer value when value >= minimum && value <= maximum -> Ok value
                    | _ -> fail path "integer outside its range"
            Write =
                fun value ->
                    if value < minimum || value > maximum then
                        invalidArg (nameof value) "Integer outside its wire range."

                    JsonValue.Integer value
            Schema = Schema.Integer(minimum, maximum)
            Definitions = Map.empty
        }

    let boolean: Codec<bool> =
        {
            Read =
                fun path element ->
                    match element with
                    | JsonValue.Boolean value -> Ok value
                    | _ -> fail path "expected a boolean"
            Write = JsonValue.Boolean
            Schema = Schema.Boolean
            Definitions = Map.empty
        }

    /// RFC 3339 UTC with exactly six fractional digits, matching ledger precision.
    let instant: Codec<DateTimeOffset> =
        {
            Read =
                fun path element ->
                    text.Read path element
                    |> Result.bind (fun raw ->

                        match
                            DateTimeOffset.TryParseExact(
                                raw,
                                JsonFormats.InstantFormat,
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
                            )
                        with
                        | true, value when
                            value.ToString(JsonFormats.InstantFormat, CultureInfo.InvariantCulture) =
                                raw
                            ->
                            Ok value
                        | _ ->
                            fail path "expected an RFC 3339 UTC instant with six fractional digits")
            Write =
                fun value ->
                    if value.Ticks % 10L <> 0L then
                        invalidArg (nameof value) "Use microsecond precision."

                    JsonValue.String(
                        value
                            .ToUniversalTime()
                            .ToString(JsonFormats.InstantFormat, CultureInfo.InvariantCulture)
                    )
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
                fun path element ->
                    text.Read path element
                    |> Result.bind (fun raw ->

                        match cases |> List.tryFind (snd >> (=) raw) with
                        | Some(value, _) -> Ok value
                        | None -> fail path "unknown token")
            Write =
                fun value ->
                    let _, token = cases |> List.find (fst >> (=) value)
                    JsonValue.String token
            Schema = Schema.OneOf(cases |> List.map (snd >> Schema.Const), None)
            Definitions = Map.empty
        }

    let nullable (codec: Codec<'T>) : Codec<'T option> =
        {
            Read =
                fun path element ->
                    if element = JsonValue.Null then
                        Ok None
                    else
                        codec.Read path element |> Result.map Some
            Write =
                fun value ->
                    match value with
                    | Some item -> codec.Write item
                    | None -> JsonValue.Null
            Schema = Schema.OneOf([ codec.Schema; Schema.Null ], None)
            Definitions = codec.Definitions
        }

    let list maxItems (codec: Codec<'T>) : Codec<'T list> =
        {
            Read =
                fun path element ->
                    match element with
                    | JsonValue.Array values when values.Length <= maxItems ->
                        values
                        |> List.indexed
                        |> List.fold
                            (fun state (index, item) ->
                                state
                                |> Result.bind (fun previous ->
                                    codec.Read (sprintf "%s[%d]" path index) item
                                    |> Result.map (fun value -> value :: previous)))
                            (Ok [])
                        |> Result.map List.rev
                    | _ -> fail path "expected a bounded array"
            Write =
                fun values ->
                    if values.Length > maxItems then
                        invalidArg (nameof values) "Array exceeds its wire limit."

                    values |> List.map codec.Write |> JsonValue.Array
            Schema = Schema.Array(codec.Schema, 0, maxItems)
            Definitions = codec.Definitions
        }

    let map read write (codec: Codec<'T>) : Codec<'U> =
        {
            Read = fun path element -> codec.Read path element |> Result.map read
            Write = fun value -> codec.Write(write value)
            Schema = codec.Schema
            Definitions = codec.Definitions
        }

    /// Preserve an admitted representation while its typed interpretation can still refuse.
    let mapResult read write (codec: Codec<'T>) : Codec<'U> =
        {
            Read = fun path element -> codec.Read path element |> Result.bind (read path)
            Write = fun value -> codec.Write(write value)
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
                fun value ->
                    if not (accepts value) then
                        invalidArg (nameof value) "Value outside its wire grammar."

                    codec.Write value
            Schema = schema
        }

    let boundedList minimum maximum codec =
        orderedBounds 0 Int32.MaxValue minimum maximum "Use ordered array bounds."

        list maximum codec
        |> constrained (Schema.Array(codec.Schema, minimum, maximum)) (fun values ->
            List.length values >= minimum)

    let constant token : Codec<unit> = enumeration [ (), token ]

    // ----- closed records -----------------------------------------------------------------

    let record (ctor: 'Ctor) : Fields<'Whole, 'Ctor> =
        {
            ReadFields = fun _ _ -> Ok ctor
            WriteFields = fun _ -> []
            Properties = []
            PropertyDefinitions = Map.empty
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
                        match JsonValue.tryMember name element with
                        | Some value -> codec.Read (path + "." + name) value |> Result.map apply
                        | _ -> fail path ("missing member " + name))
            WriteFields = fun whole -> fields.WriteFields whole @ [ name, codec.Write(get whole) ]
            Properties = fields.Properties @ [ name, codec.Schema ]
            PropertyDefinitions =
                SchemaDefinitions.merge fields.PropertyDefinitions codec.Definitions
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
                        match JsonValue.tryMember name element with
                        | None -> Ok(apply None)
                        | Some value ->
                            codec.Read (path + "." + name) value |> Result.map (Some >> apply))
            WriteFields =
                fun whole ->
                    fields.WriteFields whole
                    @ (get whole
                       |> Option.map (fun value -> [ name, codec.Write value ])
                       |> Option.defaultValue [])
            Properties = fields.Properties @ [ name, codec.Schema ]
            PropertyDefinitions =
                SchemaDefinitions.merge fields.PropertyDefinitions codec.Definitions
            OptionalMembers = name :: fields.OptionalMembers
        }

    let private closed members path element =
        match element with
        | JsonValue.Object values when
            values |> List.forall (fun (name, _) -> List.contains name members)
            ->
            Ok()
        | _ -> fail path "expected a closed object"

    let build (fields: Fields<'Whole, 'Whole>) : Codec<'Whole> =
        let members = fields.Properties |> List.map fst

        {
            Read =
                fun path element ->
                    closed members path element
                    |> Result.bind (fun () -> fields.ReadFields path element)
            Write = fun value -> JsonValue.Object(fields.WriteFields value)
            Schema = Schema.Object(fields.Properties, fields.OptionalMembers)
            Definitions = fields.PropertyDefinitions
        }

    /// A record constructor may validate relationships between separately encoded members.
    let buildResult describe (fields: Fields<'Whole, Result<'Whole, 'Error>>) : Codec<'Whole> =
        {
            ReadFields =
                fun path element ->
                    fields.ReadFields path element |> Result.bind (Result.mapError (describe path))
            WriteFields = fields.WriteFields
            Properties = fields.Properties
            PropertyDefinitions = fields.PropertyDefinitions
            OptionalMembers = fields.OptionalMembers
        }
        |> build

    // ----- tagged unions ------------------------------------------------------------------

    /// A case whose payload members sit beside the tag member in one object.
    let variant
        tag
        (ctor: 'P -> 'U)
        (tryGet: 'U -> 'P option)
        (fields: Fields<'P, 'P>)
        : Variant<'U> =
        {
            Tag = tag
            ReadVariant = fun path element -> fields.ReadFields path element |> Result.map ctor
            TryWrite =
                fun value ->
                    tryGet value
                    |> Option.map (fun payload -> JsonValue.Object(fields.WriteFields payload))
            VariantSchema = Schema.Object(fields.Properties, fields.OptionalMembers)
            VariantDefinitions = fields.PropertyDefinitions
        }

    /// A case that carries nothing but its tag.
    let unitVariant tag (value: 'U) : Variant<'U> =
        variant
            tag
            (fun () -> value)
            (fun candidate -> if candidate = value then Some() else None)
            (record ())

    /// Flatten additional typed fields alongside an inner alternative without serializing it.
    let extendVariant
        (ctor: 'Extra -> 'Inner -> 'Outer)
        (tryGet: 'Outer -> ('Extra * 'Inner) option)
        (extra: Fields<'Extra, 'Extra>)
        (source: Variant<'Inner>)
        : Variant<'Outer> =
        let properties, optional =
            match source.VariantSchema with
            | Schema.Object(properties, optional) -> properties, optional
            | _ -> invalidArg (nameof source) "Use an object payload variant."

        {
            Tag = source.Tag
            ReadVariant =
                fun path element ->
                    extra.ReadFields path element
                    |> Result.bind (fun value ->
                        source.ReadVariant path element |> Result.map (ctor value))
            TryWrite =
                fun value ->
                    tryGet value
                    |> Option.bind (fun (added, inner) ->
                        source.TryWrite inner
                        |> Option.map (function
                            | JsonValue.Object fields ->
                                JsonValue.Object(extra.WriteFields added @ fields)
                            | _ -> invalidOp "An object variant wrote a non-object payload."))
            VariantSchema =
                Schema.Object(extra.Properties @ properties, extra.OptionalMembers @ optional)
            VariantDefinitions =
                SchemaDefinitions.merge extra.PropertyDefinitions source.VariantDefinitions
        }

    let taggedOn tagMember (name: string) (variants: Variant<'U> list) : Codec<'U> =
        let cases = variants
        let caseName (case: Variant<'U>) = name + "." + case.Tag

        let tags = List.map _.Tag cases

        if List.distinct tags <> tags then
            invalidArg (nameof variants) "Tagged alternatives must have unique tags."

        let properties (case: Variant<'U>) =
            match case.VariantSchema with
            | Schema.Object(properties, optional) ->
                let names = tagMember :: List.map fst properties

                if List.distinct names <> names then
                    invalidArg
                        (nameof variants)
                        "Tagged payload fields must be unique and exclude the discriminator."

                properties, optional
            | _ -> invalidArg (nameof variants) "Tagged alternatives require object payloads."

        let definitions =
            cases
            |> List.fold
                (fun state case ->
                    let fields, optional = properties case

                    Map.add
                        (caseName case)
                        (Schema.Object((tagMember, Schema.Const case.Tag) :: fields, optional))
                        (SchemaDefinitions.merge state case.VariantDefinitions))
                Map.empty

        let union =
            Schema.OneOf(
                cases |> List.map (caseName >> Schema.Ref),
                Some(tagMember, cases |> List.map (fun case -> case.Tag, caseName case))
            )

        {
            Read =
                fun path element ->
                    match JsonValue.tryMember tagMember element with
                    | Some(JsonValue.String tag) ->
                        match cases |> List.tryFind (fun case -> case.Tag = tag) with
                        | Some case ->
                            let fields, _ = properties case

                            closed (tagMember :: List.map fst fields) path element
                            |> Result.bind (fun () -> case.ReadVariant path element)
                        | None -> fail (path + "." + tagMember) "unknown tag"
                    | _ -> fail path ("missing member " + tagMember)
            Write =
                fun value ->
                    cases
                    |> List.tryPick (fun case ->
                        case.TryWrite value
                        |> Option.map (function
                            | JsonValue.Object fields ->
                                JsonValue.Object((tagMember, JsonValue.String case.Tag) :: fields)
                            | _ -> invalidOp "A tagged alternative wrote a non-object payload."))
                    |> Option.defaultWith (fun () -> invalidOp "A union codec is incomplete.")
            Schema = Schema.Ref name
            Definitions = Map.add name union definitions
        }

    let tagged name variants = taggedOn "kind" name variants

    /// Share equal payload schemas while retaining the same individual typed alternatives.
    let groupedTaggedOn tagMember name (variants: Variant<'T> list) =
        let codec = taggedOn tagMember name variants
        let groups = variants |> List.groupBy _.VariantSchema

        let shapes =
            groups
            |> List.map (fun (schema, cases) ->
                let groupName = name + "." + cases.Head.Tag
                let tags = cases |> List.map (fun case -> case.Tag)
                let tagSchema = Schema.OneOf(tags |> List.map Schema.Const, None)

                let shape =
                    match schema with
                    | Schema.Object(fields, optional) ->
                        Schema.Object((tagMember, tagSchema) :: fields, optional)
                    | _ ->
                        invalidArg (nameof variants) "Tagged alternatives require object payloads."

                groupName, shape, tags)

        let definitions =
            variants
            |> List.fold
                (fun state case -> SchemaDefinitions.merge state case.VariantDefinitions)
                Map.empty
            |> fun state ->
                shapes
                |> List.fold (fun state (group, schema, _) -> Map.add group schema state) state

        let mapping =
            shapes
            |> List.collect (fun (group, _, tags) -> tags |> List.map (fun tag -> tag, group))

        let schema =
            Schema.OneOf(
                shapes |> List.map (fun (group, _, _) -> Schema.Ref group),
                Some(tagMember, mapping)
            )

        { codec with
            Definitions = Map.add name schema definitions
        }

    let nullValue: Codec<unit> =
        {
            Read =
                fun path value ->
                    if value = JsonValue.Null then
                        Ok()
                    else
                        fail path "expected null"
            Write = fun () -> JsonValue.Null
            Schema = Schema.Null
            Definitions = Map.empty
        }

    let branch ctor tryGet (codec: Codec<'T>) =
        {
            Tag = ""
            ReadVariant = fun path element -> codec.Read path element |> Result.map ctor
            TryWrite = fun value -> tryGet value |> Option.map codec.Write
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
                fun value ->
                    cases
                    |> List.tryPick (fun case -> case.TryWrite value)
                    |> Option.defaultWith (fun () -> invalidOp "A union codec is incomplete.")
            Schema = Schema.Ref name
            Definitions =
                cases
                |> List.fold
                    (fun state case -> SchemaDefinitions.merge state case.VariantDefinitions)
                    Map.empty
                |> Map.add name (Schema.OneOf(cases |> List.map _.VariantSchema, None))
        }

    let rec private objectShape definitions =
        function
        | Schema.Ref name -> objectShape definitions (Map.find name definitions)
        | Schema.Object(properties, optional) -> properties, optional
        | _ -> invalidArg "codec" "A format member requires an object codec."

    let private withMember name (memberCodec: Codec<unit>) (codec: Codec<'T>) : Codec<'T> =
        let properties, optional = objectShape codec.Definitions codec.Schema

        if properties |> List.exists (fst >> (=) name) then
            invalidArg (nameof codec) "The object already declares the member."

        {
            Read =
                fun path value ->
                    match value with
                    | JsonValue.Object members ->
                        match members |> List.partition (fst >> (=) name) with
                        | [ _, memberValue ], remaining ->
                            memberCodec.Read (path + "." + name) memberValue
                            |> Result.bind (fun () -> codec.Read path (JsonValue.Object remaining))
                        | _ -> fail (path + "." + name) "missing or duplicate member"
                    | _ -> fail path "expected an object"
            Write =
                fun value ->
                    match codec.Write value with
                    | JsonValue.Object members ->
                        JsonValue.Object(members @ [ name, memberCodec.Write() ])
                    | _ -> invalidOp "A member requires an object codec."
            Schema = Schema.Object(properties @ [ name, memberCodec.Schema ], optional)
            Definitions = SchemaDefinitions.merge codec.Definitions memberCodec.Definitions
        }

    /// RecordFormat chooses the format number and owns the named durable codec value.
    let withFormat format codec =
        let number = integer format format |> map (fun _ -> ()) (fun () -> format)
        withMember "format" number codec

    // ----- bytes --------------------------------------------------------------------------

    let encode (codec: Codec<'T>) (value: 'T) = codec.Write value |> WireWriter.bytes

    let decode maximumBytes (codec: Codec<'T>) bytes =
        StrictJson.read maximumBytes bytes |> Result.bind (codec.Read "$")

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
