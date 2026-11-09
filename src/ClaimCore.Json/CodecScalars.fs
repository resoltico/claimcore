namespace ClaimCore.Json

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Encodings.Web
open System.Text.Json

module internal CodecScalars =
    let fail path reason = Error { Path = path; Reason = reason }

    let text: Codec<string> =
        let read =
            fun path value ->
                match value with
                | JsonValue.String text when JsonValue.isText text && not (text.Contains '\u0000') ->
                    Ok text
                | _ -> fail path "expected Unicode scalar text without NUL or noncharacters"

        {
            Reader = read
            Validation = fun path value -> read path value |> Result.map ignore
            Writer =
                fun value ->
                    if not (JsonValue.isText value) || value.Contains '\u0000' then
                        invalidArg
                            (nameof value)
                            "Expected Unicode scalar text without NUL or noncharacters."

                    JsonValue.String value
            Shape = Schema.String(None, None, None, None, None)
            NamedSchemas = Map.empty
        }

    let private orderedBounds lower upper minimum maximum message =
        if minimum < lower || maximum > upper || minimum > maximum then
            invalidArg (nameof minimum) message

    let boundedText minimum maximum =
        orderedBounds 0 Int32.MaxValue minimum maximum "Use ordered text-length bounds."

        let within (value: string) =
            let length = value.EnumerateRunes() |> Seq.length
            length >= minimum && length <= maximum

        let read =
            fun path element ->
                text.Reader path element
                |> Result.bind (fun value ->
                    if within value then
                        Ok value
                    else
                        fail path "text outside its length bounds")

        { text with
            Reader = read
            Validation = fun path value -> read path value |> Result.map ignore
            Writer =
                fun value ->
                    if not (within value) then
                        invalidArg (nameof value) "Text outside its wire bounds."

                    text.Writer value
            Shape = Schema.String(None, None, Some minimum, Some maximum, None)
        }

    /// A wire byte limit is independent of character admission and applies to retained text too.
    let boundedUtf8Text minimum maximumBytes =
        orderedBounds 0 Int32.MaxValue minimum maximumBytes "Use ordered UTF-8 byte bounds."

        let within (value: string) =
            Encoding.UTF8.GetByteCount value <= maximumBytes

        let scalar = boundedText minimum maximumBytes

        let read =
            fun path value ->
                scalar.Reader path value
                |> Result.bind (fun text ->
                    if within text then
                        Ok text
                    else
                        fail path "text exceeds its UTF-8 byte limit")

        { scalar with
            Reader = read
            Validation = fun path value -> read path value |> Result.map ignore
            Writer =
                fun value ->
                    if not (within value) then
                        invalidArg (nameof value) "Text exceeds its UTF-8 byte limit."

                    scalar.Writer value
            Shape = Schema.String(None, None, Some minimum, Some maximumBytes, Some maximumBytes)
        }

    /// Registers the codec's schema once under a name and refers to it from users.
    let named name (codec: Codec<'T>) : Codec<'T> =
        match codec.Shape with
        | Schema.Ref current when current = name ->
            if not (Map.containsKey name codec.NamedSchemas) then
                invalidArg (nameof codec) "A named codec requires its schema definition."

            codec
        | schema ->
            match Map.tryFind name codec.NamedSchemas with
            | Some existing when existing <> schema ->
                invalidArg (nameof name) "A schema name cannot replace an existing definition."
            | _ ->
                { codec with
                    Shape = Schema.Ref name
                    NamedSchemas = Map.add name schema codec.NamedSchemas
                }

    /// Semantic vocabulary identity accompanies structure without changing its wire grammar.
    let vocabulary name (codec: Codec<'T>) =
        { codec with
            Shape = Schema.Vocabulary(name, codec.Shape)
        }

    let uuid: Codec<Guid> =
        let read =
            fun path element ->
                text.Reader path element
                |> Result.bind (fun raw ->

                    match Guid.TryParseExact(raw, "D") with
                    | true, value when value <> Guid.Empty && value.ToString "D" = raw -> Ok value
                    | _ -> fail path "expected a canonical nonzero UUID")

        {
            Reader = read
            Validation = fun path value -> read path value |> Result.map ignore
            Writer =
                fun value ->
                    if value = Guid.Empty then
                        invalidArg (nameof value) "Use a nonzero UUID."

                    JsonValue.String(value.ToString "D")
            Shape =
                Schema.String(
                    Some
                        "^(?!00000000-0000-0000-0000-000000000000$)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
                    Some "uuid",
                    Some 36,
                    Some 36,
                    None
                )
            NamedSchemas = Map.empty
        }
        |> named "Uuid"

    let integer minimum maximum : Codec<int64> =
        orderedBounds
            (-JsonValue.MaximumInteger)
            JsonValue.MaximumInteger
            minimum
            maximum
            "Use ordered safe-integer bounds."

        let read =
            fun path element ->
                match element with
                | JsonValue.Integer value when value >= minimum && value <= maximum -> Ok value
                | _ -> fail path "integer outside its range"

        {
            Reader = read
            Validation = fun path value -> read path value |> Result.map ignore
            Writer =
                fun value ->
                    if value < minimum || value > maximum then
                        invalidArg (nameof value) "Integer outside its wire range."

                    JsonValue.Integer value
            Shape = Schema.Integer(minimum, maximum)
            NamedSchemas = Map.empty
        }

    let boolean: Codec<bool> =
        let read =
            fun path element ->
                match element with
                | JsonValue.Boolean value -> Ok value
                | _ -> fail path "expected a boolean"

        {
            Reader = read
            Validation = fun path value -> read path value |> Result.map ignore
            Writer = JsonValue.Boolean
            Shape = Schema.Boolean
            NamedSchemas = Map.empty
        }

    /// RFC 3339 UTC with exactly six fractional digits, matching ledger precision.
    let instant: Codec<DateTimeOffset> =
        let read =
            fun path element ->
                text.Reader path element
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
                    | _ -> fail path "expected an RFC 3339 UTC instant with six fractional digits")

        {
            Reader = read
            Validation = fun path value -> read path value |> Result.map ignore
            Writer =
                fun value ->
                    if value.Ticks % 10L <> 0L then
                        invalidArg (nameof value) "Use microsecond precision."

                    JsonValue.String(
                        value
                            .ToUniversalTime()
                            .ToString(JsonFormats.InstantFormat, CultureInfo.InvariantCulture)
                    )
            Shape =
                Schema.String(
                    Some "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{6}Z$",
                    Some "date-time",
                    Some 27,
                    Some 27,
                    None
                )
            NamedSchemas = Map.empty
        }

    /// A closed set of string tokens mapped to values.
    let enumeration (cases: ('T * string) list) : Codec<'T> =
        let read =
            fun path element ->
                text.Reader path element
                |> Result.bind (fun raw ->

                    match cases |> List.tryFind (snd >> (=) raw) with
                    | Some(value, _) -> Ok value
                    | None -> fail path "unknown token")

        {
            Reader = read
            Validation = fun path value -> read path value |> Result.map ignore
            Writer =
                fun value ->
                    let _, token = cases |> List.find (fst >> (=) value)
                    JsonValue.String token
            Shape = Schema.OneOf(cases |> List.map (snd >> Schema.Const), None)
            NamedSchemas = Map.empty
        }

    let nullable (codec: Codec<'T>) : Codec<'T option> =
        {
            Validation =
                fun path value ->
                    if value = JsonValue.Null then
                        Ok()
                    else
                        codec.Validation path value
            Reader =
                fun path element ->
                    if element = JsonValue.Null then
                        Ok None
                    else
                        codec.Reader path element |> Result.map Some
            Writer =
                fun value ->
                    match value with
                    | Some item -> codec.Writer item
                    | None -> JsonValue.Null
            Shape = Schema.OneOf([ codec.Shape; Schema.Null ], None)
            NamedSchemas = codec.NamedSchemas
        }

    let list maxItems (codec: Codec<'T>) : Codec<'T list> =
        {
            Validation =
                fun path value ->
                    match value with
                    | JsonValue.Array values when values.Length <= maxItems ->
                        values
                        |> List.indexed
                        |> List.fold
                            (fun result (index, item) ->
                                result
                                |> Result.bind (fun () ->
                                    codec.Validation (sprintf "%s[%d]" path index) item))
                            (Ok())
                    | _ -> fail path "expected a bounded array"
            Reader =
                fun path element ->
                    match element with
                    | JsonValue.Array values when values.Length <= maxItems ->
                        values
                        |> List.indexed
                        |> List.fold
                            (fun state (index, item) ->
                                state
                                |> Result.bind (fun previous ->
                                    codec.Reader (sprintf "%s[%d]" path index) item
                                    |> Result.map (fun value -> value :: previous)))
                            (Ok [])
                        |> Result.map List.rev
                    | _ -> fail path "expected a bounded array"
            Writer =
                fun values ->
                    if values.Length > maxItems then
                        invalidArg (nameof values) "Array exceeds its wire limit."

                    values |> List.map codec.Writer |> JsonValue.Array
            Shape = Schema.Array(codec.Shape, 0, maxItems)
            NamedSchemas = codec.NamedSchemas
        }

    let map read write (codec: Codec<'T>) : Codec<'U> =
        {
            Reader = fun path element -> codec.Reader path element |> Result.map read
            Validation = codec.Validation
            Writer = fun value -> codec.Writer(write value)
            Shape = codec.Shape
            NamedSchemas = codec.NamedSchemas
        }

    /// Preserve an admitted representation while its typed interpretation can still refuse.
    let interpret read write (codec: Codec<'T>) : Codec<'U> =
        {
            Reader = fun path element -> codec.Reader path element |> Result.bind (read path)
            Validation = codec.Validation
            Writer = fun value -> codec.Writer(write value)
            Shape = codec.Shape
            NamedSchemas = codec.NamedSchemas
        }

    /// Semantic refinement leaves the structural contract unchanged.
    let refine accepts (codec: Codec<'T>) =
        { codec with
            Reader =
                fun path element ->
                    codec.Reader path element
                    |> Result.bind (fun value ->
                        if accepts value then
                            Ok value
                        else
                            fail path "value outside its wire grammar")
            Writer =
                fun value ->
                    if not (accepts value) then
                        invalidArg (nameof value) "Value outside its wire grammar."

                    codec.Writer value
        }

    /// Structural restrictions must supply a raw validator, independently of typed interpretation.
    let restrict schema validate (codec: Codec<'T>) =
        let structural path value =
            codec.Validation path value |> Result.bind (fun () -> validate path value)

        { codec with
            Validation = structural
            Reader =
                fun path value ->
                    structural path value |> Result.bind (fun () -> codec.Reader path value)
            Writer =
                fun value ->
                    let raw = codec.Writer value

                    match structural "$" raw with
                    | Ok() -> raw
                    | Error _ -> invalidArg (nameof value) "Value outside its structural grammar."
            Shape = schema
        }

    let patternedText minimum maximum pattern format =
        let expression =
            System.Text.RegularExpressions.Regex(
                pattern,
                System.Text.RegularExpressions.RegexOptions.CultureInvariant
            )

        boundedText minimum maximum
        |> restrict
            (Schema.String(Some pattern, format, Some minimum, Some maximum, None))
            (fun path value ->
                match value with
                | JsonValue.String text when expression.IsMatch text -> Ok()
                | _ -> fail path "text outside its declared pattern")

    /// A finite structural partition is shared by schema emission and native validation.
    let enumPartition values selected rejected rejectedKey (codec: Codec<'T>) =
        codec
        |> restrict
            (Schema.EnumPartition(codec.Shape, values, selected, rejected, rejectedKey))
            (fun path value ->
                match JsonValue.tryMember selected value, JsonValue.tryMember rejected value with
                | Some(JsonValue.Array offered), Some(JsonValue.Array blocked) ->
                    let unavailable = blocked |> List.choose (JsonValue.tryMember rejectedKey)

                    if
                        unavailable.Length = blocked.Length
                        && (List.distinct unavailable).Length = unavailable.Length
                        && (values
                            |> List.forall (fun token ->
                                List.contains (JsonValue.String token) offered
                                <> List.contains (JsonValue.String token) unavailable))
                    then
                        Ok()
                    else
                        fail (path + "." + rejected) "expected a closed enum partition"
                | _ -> fail path "expected enum partition members")

    let boundedList minimum maximum codec =
        orderedBounds 0 Int32.MaxValue minimum maximum "Use ordered array bounds."

        list maximum codec
        |> restrict (Schema.Array(codec.Shape, minimum, maximum)) (fun path value ->
            match value with
            | JsonValue.Array values when values.Length >= minimum -> Ok()
            | _ -> fail path "array outside its lower bound")

    let constant token : Codec<unit> = enumeration [ (), token ]
