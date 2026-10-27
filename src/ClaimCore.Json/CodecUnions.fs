namespace ClaimCore.Json

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Encodings.Web
open System.Text.Json

module internal CodecUnions =
    open CodecScalars
    open CodecRecords

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
