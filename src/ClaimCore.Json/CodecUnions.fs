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
            Discriminator = tag
            SchemaName = None
            Validation = fields.Validation
            ReadCase = fun path element -> fields.ReadMembers path element |> Result.map ctor
            WriteCase =
                fun value ->
                    tryGet value
                    |> Option.map (fun payload -> JsonValue.Object(fields.WriteMembers payload))
            CaseShape = Schema.Object(fields.MemberShapes, fields.OptionalNames)
            CaseDefinitions = fields.MemberDefinitions
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
            match source.CaseShape with
            | Schema.Object(properties, optional) -> properties, optional
            | _ -> invalidArg (nameof source) "Use an object payload variant."

        {
            Discriminator = source.Discriminator
            SchemaName = None
            Validation =
                fun path value ->
                    extra.Validation path value
                    |> Result.bind (fun () -> source.Validation path value)
            ReadCase =
                fun path element ->
                    extra.ReadMembers path element
                    |> Result.bind (fun value ->
                        source.ReadCase path element |> Result.map (ctor value))
            WriteCase =
                fun value ->
                    tryGet value
                    |> Option.bind (fun (added, inner) ->
                        source.WriteCase inner
                        |> Option.map (function
                            | JsonValue.Object fields ->
                                JsonValue.Object(extra.WriteMembers added @ fields)
                            | _ -> invalidOp "An object variant wrote a non-object payload."))
            CaseShape =
                Schema.Object(extra.MemberShapes @ properties, extra.OptionalNames @ optional)
            CaseDefinitions = SchemaDefinitions.merge extra.MemberDefinitions source.CaseDefinitions
        }

    let taggedOn tagMember (name: string) (variants: Variant<'U> list) : Codec<'U> =
        let cases = variants

        let caseName (case: Variant<'U>) =
            case.SchemaName |> Option.defaultValue (name + "." + case.Discriminator)

        let tags = List.map _.Discriminator cases

        if List.distinct tags <> tags then
            invalidArg (nameof variants) "Tagged alternatives must have unique tags."

        let properties (case: Variant<'U>) =
            match case.CaseShape with
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

                    SchemaDefinitions.merge
                        (SchemaDefinitions.merge state case.CaseDefinitions)
                        (Map.ofList
                            [
                                caseName case,
                                Schema.Object(
                                    (tagMember, Schema.Const case.Discriminator) :: fields,
                                    optional
                                )
                            ]))
                Map.empty

        let union =
            Schema.OneOf(
                cases |> List.map (caseName >> Schema.Ref),
                Some(tagMember, cases |> List.map (fun case -> case.Discriminator, caseName case))
            )

        {
            Validation =
                fun path element ->
                    match JsonValue.tryMember tagMember element with
                    | Some(JsonValue.String tag) ->
                        match cases |> List.tryFind (fun case -> case.Discriminator = tag) with
                        | Some case ->
                            let fields, _ = properties case

                            closed (tagMember :: List.map fst fields) path element
                            |> Result.bind (fun () -> case.Validation path element)
                        | None -> fail (path + "." + tagMember) "unknown tag"
                    | _ -> fail path ("missing member " + tagMember)
            Reader =
                fun path element ->
                    match JsonValue.tryMember tagMember element with
                    | Some(JsonValue.String tag) ->
                        match cases |> List.tryFind (fun case -> case.Discriminator = tag) with
                        | Some case ->
                            let fields, _ = properties case

                            closed (tagMember :: List.map fst fields) path element
                            |> Result.bind (fun () -> case.ReadCase path element)
                        | None -> fail (path + "." + tagMember) "unknown tag"
                    | _ -> fail path ("missing member " + tagMember)
            Writer =
                fun value ->
                    cases
                    |> List.tryPick (fun case ->
                        case.WriteCase value
                        |> Option.map (function
                            | JsonValue.Object fields ->
                                JsonValue.Object(
                                    (tagMember, JsonValue.String case.Discriminator) :: fields
                                )
                            | _ -> invalidOp "A tagged alternative wrote a non-object payload."))
                    |> Option.defaultWith (fun () -> invalidOp "A union codec is incomplete.")
            Shape = Schema.Ref name
            NamedSchemas = Map.add name union definitions
        }

    let tagged name variants = taggedOn "kind" name variants

    /// Share equal payload schemas while retaining the same individual typed alternatives.
    let groupedTaggedOn tagMember name (variants: Variant<'T> list) =
        let codec = taggedOn tagMember name variants
        let groups = variants |> List.groupBy _.CaseShape

        let shapes =
            groups
            |> List.map (fun (schema, cases) ->
                let groupName = name + "." + cases.Head.Discriminator
                let tags = cases |> List.map (fun case -> case.Discriminator)
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
                (fun state case -> SchemaDefinitions.merge state case.CaseDefinitions)
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
            NamedSchemas = Map.add name schema definitions
        }

    let nullValue: Codec<unit> =
        {
            Validation =
                fun path value ->
                    if value = JsonValue.Null then
                        Ok()
                    else
                        fail path "expected null"
            Reader =
                fun path value ->
                    if value = JsonValue.Null then
                        Ok()
                    else
                        fail path "expected null"
            Writer = fun () -> JsonValue.Null
            Shape = Schema.Null
            NamedSchemas = Map.empty
        }

    let branch ctor tryGet (codec: Codec<'T>) =
        {
            Discriminator = ""
            SchemaName = None
            Validation = codec.Validation
            ReadCase = fun path element -> codec.Reader path element |> Result.map ctor
            WriteCase = fun value -> tryGet value |> Option.map codec.Writer
            CaseShape = codec.Shape
            CaseDefinitions = codec.NamedSchemas
        }

    let choice name (cases: Variant<'T> list) : Codec<'T> =
        let read path element =
            let values =
                cases
                |> List.choose (fun case ->
                    match case.ReadCase path element with
                    | Ok value -> Some value
                    | Error _ -> None)

            match values with
            | [ value ] -> Ok value
            | _ -> fail path "expected exactly one variant"

        {
            Validation =
                fun path value ->
                    let matched =
                        cases |> List.filter (fun case -> case.Validation path value |> Result.isOk)

                    match matched with
                    | [ _ ] -> Ok()
                    | _ -> fail path "expected exactly one structural variant"
            Reader = read
            Writer =
                fun value ->
                    cases
                    |> List.tryPick (fun case -> case.WriteCase value)
                    |> Option.defaultWith (fun () -> invalidOp "A union codec is incomplete.")
            Shape = Schema.Ref name
            NamedSchemas =
                cases
                |> List.fold
                    (fun state case -> SchemaDefinitions.merge state case.CaseDefinitions)
                    Map.empty
                |> Map.add name (Schema.OneOf(cases |> List.map _.CaseShape, None))
        }

    let rec private objectShape definitions =
        function
        | Schema.Ref name -> objectShape definitions (Map.find name definitions)
        | Schema.Object(properties, optional) -> properties, optional
        | _ -> invalidArg "codec" "A format member requires an object codec."

    let private withMember name (memberCodec: Codec<unit>) (codec: Codec<'T>) : Codec<'T> =
        let properties, optional = objectShape codec.NamedSchemas codec.Shape

        if properties |> List.exists (fst >> (=) name) then
            invalidArg (nameof codec) "The object already declares the member."

        {
            Validation =
                fun path value ->
                    match value with
                    | JsonValue.Object members ->
                        match members |> List.partition (fst >> (=) name) with
                        | [ _, memberValue ], remaining ->
                            memberCodec.Validation (path + "." + name) memberValue
                            |> Result.bind (fun () ->
                                codec.Validation path (JsonValue.Object remaining))
                        | _ -> fail (path + "." + name) "missing or duplicate member"
                    | _ -> fail path "expected an object"
            Reader =
                fun path value ->
                    match value with
                    | JsonValue.Object members ->
                        match members |> List.partition (fst >> (=) name) with
                        | [ _, memberValue ], remaining ->
                            memberCodec.Reader (path + "." + name) memberValue
                            |> Result.bind (fun () ->
                                codec.Reader path (JsonValue.Object remaining))
                        | _ -> fail (path + "." + name) "missing or duplicate member"
                    | _ -> fail path "expected an object"
            Writer =
                fun value ->
                    match codec.Writer value with
                    | JsonValue.Object members ->
                        JsonValue.Object(members @ [ name, memberCodec.Writer() ])
                    | _ -> invalidOp "A member requires an object codec."
            Shape = Schema.Object(properties @ [ name, memberCodec.Shape ], optional)
            NamedSchemas = SchemaDefinitions.merge codec.NamedSchemas memberCodec.NamedSchemas
        }

    /// RecordFormat chooses the format number and owns the named durable codec value.
    let withFormat format codec =
        let number = integer format format |> map (fun _ -> ()) (fun () -> format)
        withMember "format" number codec

    let withLiteral name token codec = withMember name (constant token) codec
