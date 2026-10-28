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
