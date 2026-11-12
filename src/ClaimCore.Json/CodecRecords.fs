namespace ClaimCore.Json

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Encodings.Web
open System.Text.Json

module internal CodecRecords =
    open CodecScalars

    let record (ctor: 'Ctor) : Fields<'Whole, 'Ctor> =
        {
            ReadMembers = fun _ _ -> Ok ctor
            Validation = fun _ _ -> Ok()
            WriteMembers = fun _ -> []
            MemberShapes = []
            MemberDefinitions = Map.empty
            OptionalNames = []
        }

    let field
        (name: string)
        (get: 'Whole -> 'F)
        (codec: Codec<'F>)
        (fields: Fields<'Whole, 'F -> 'Rest>)
        : Fields<'Whole, 'Rest> =
        {
            Validation =
                fun path element ->
                    fields.Validation path element
                    |> Result.bind (fun () ->
                        match JsonValue.tryMember name element with
                        | Some value -> codec.Validation (path + "." + name) value
                        | None -> fail path ("missing member " + name))
            ReadMembers =
                fun path element ->
                    fields.ReadMembers path element
                    |> Result.bind (fun apply ->
                        match JsonValue.tryMember name element with
                        | Some value -> codec.Reader (path + "." + name) value |> Result.map apply
                        | _ -> fail path ("missing member " + name))
            WriteMembers =
                fun whole -> fields.WriteMembers whole @ [ name, codec.Writer(get whole) ]
            MemberShapes = fields.MemberShapes @ [ name, codec.Shape ]
            MemberDefinitions = SchemaDefinitions.merge fields.MemberDefinitions codec.NamedSchemas
            OptionalNames = fields.OptionalNames
        }

    let fixedField name token (fields: Fields<'Whole, 'Ctor>) : Fields<'Whole, 'Ctor> =
        let codec = constant token

        { fields with
            Validation =
                fun path element ->
                    fields.Validation path element
                    |> Result.bind (fun () ->
                        match JsonValue.tryMember name element with
                        | Some value -> codec.Validation (path + "." + name) value
                        | None ->
                            Error
                                {
                                    Path = path
                                    Reason = "missing member " + name
                                })
            ReadMembers =
                fun path element ->
                    fields.ReadMembers path element
                    |> Result.bind (fun ctor ->
                        match JsonValue.tryMember name element with
                        | Some value ->
                            codec.Reader (path + "." + name) value |> Result.map (fun () -> ctor)
                        | _ ->
                            Error
                                {
                                    Path = path
                                    Reason = "missing member " + name
                                })
            WriteMembers = fun value -> fields.WriteMembers value @ [ name, codec.Writer() ]
            MemberShapes = fields.MemberShapes @ [ name, codec.Shape ]
        }

    let optionalField
        name
        (get: 'Whole -> 'F option)
        (codec: Codec<'F>)
        (fields: Fields<'Whole, 'F option -> 'Rest>)
        : Fields<'Whole, 'Rest> =
        {
            Validation =
                fun path element ->
                    fields.Validation path element
                    |> Result.bind (fun () ->
                        match JsonValue.tryMember name element with
                        | None -> Ok()
                        | Some value -> codec.Validation (path + "." + name) value)
            ReadMembers =
                fun path element ->
                    fields.ReadMembers path element
                    |> Result.bind (fun apply ->
                        match JsonValue.tryMember name element with
                        | None -> Ok(apply None)
                        | Some value ->
                            codec.Reader (path + "." + name) value |> Result.map (Some >> apply))
            WriteMembers =
                fun whole ->
                    fields.WriteMembers whole
                    @ (get whole
                       |> Option.map (fun value -> [ name, codec.Writer value ])
                       |> Option.defaultValue [])
            MemberShapes = fields.MemberShapes @ [ name, codec.Shape ]
            MemberDefinitions = SchemaDefinitions.merge fields.MemberDefinitions codec.NamedSchemas
            OptionalNames = name :: fields.OptionalNames
        }

    let closed members path element =
        match element with
        | JsonValue.Object values when
            values |> List.forall (fun (name, _) -> List.contains name members)
            && (values |> List.map fst |> List.distinct |> List.length) = values.Length
            ->
            Ok()
        | _ -> fail path "expected a closed object"

    let build (fields: Fields<'Whole, 'Whole>) : Codec<'Whole> =
        let members = fields.MemberShapes |> List.map fst

        {
            Validation =
                fun path value ->
                    closed members path value
                    |> Result.bind (fun () -> fields.Validation path value)
            Reader =
                fun path element ->
                    closed members path element
                    |> Result.bind (fun () -> fields.ReadMembers path element)
            Writer = fun value -> JsonValue.Object(fields.WriteMembers value)
            Shape = Schema.Object(fields.MemberShapes, fields.OptionalNames)
            NamedSchemas = fields.MemberDefinitions
        }

    /// Compose independently meaningful input groups in one closed wire object.
    let flatten
        get
        (part: Fields<'Part, 'Part>)
        (fields: Fields<'Whole, 'Part -> 'Rest>)
        : Fields<'Whole, 'Rest> =
        let repeated =
            Set.intersect
                (fields.MemberShapes |> List.map fst |> Set.ofList)
                (part.MemberShapes |> List.map fst |> Set.ofList)

        if not repeated.IsEmpty then
            invalidArg (nameof part) "Flattened object members must be disjoint."

        {
            Validation =
                fun path value ->
                    fields.Validation path value
                    |> Result.bind (fun () -> part.Validation path value)
            ReadMembers =
                fun path value ->
                    fields.ReadMembers path value
                    |> Result.bind (fun apply -> part.ReadMembers path value |> Result.map apply)
            WriteMembers = fun value -> fields.WriteMembers value @ part.WriteMembers(get value)
            MemberShapes = fields.MemberShapes @ part.MemberShapes
            MemberDefinitions =
                SchemaDefinitions.merge fields.MemberDefinitions part.MemberDefinitions
            OptionalNames = fields.OptionalNames @ part.OptionalNames
        }

    /// A closed object whose declared members share a value representation and retain typed keys.
    let fieldMap (members: ('Key * string * Codec<'Value>) list) : Codec<Map<'Key, 'Value>> =
        let add (fields: Fields<Map<'Key, 'Value>, Map<'Key, 'Value>>) (key, name, codec) =
            let applying: Fields<Map<'Key, 'Value>, 'Value -> Map<'Key, 'Value>> =
                {
                    Validation = fields.Validation
                    ReadMembers =
                        fun path value ->
                            fields.ReadMembers path value
                            |> Result.map (fun values item -> Map.add key item values)
                    WriteMembers = fields.WriteMembers
                    MemberShapes = fields.MemberShapes
                    MemberDefinitions = fields.MemberDefinitions
                    OptionalNames = fields.OptionalNames
                }

            field name (Map.find key) codec applying

        members |> List.fold add (record Map.empty) |> build

    /// A closed field map whose member presence is declared alongside each typed key.
    let fieldMapWithPresence
        (members: ('Key * string * bool * Codec<'Value>) list)
        : Codec<Map<'Key, 'Value>> =
        let add
            (fields: Fields<Map<'Key, 'Value>, Map<'Key, 'Value>>)
            (key, name, optional, codec)
            =
            let applying: Fields<Map<'Key, 'Value>, 'Value option -> Map<'Key, 'Value>> =
                {
                    Validation = fields.Validation
                    ReadMembers =
                        fun path value ->
                            fields.ReadMembers path value
                            |> Result.map (fun values item ->
                                match item with
                                | Some item -> Map.add key item values
                                | None -> values)
                    WriteMembers = fields.WriteMembers
                    MemberShapes = fields.MemberShapes
                    MemberDefinitions = fields.MemberDefinitions
                    OptionalNames = fields.OptionalNames
                }

            if optional then
                optionalField name (Map.tryFind key) codec applying
            else
                let required: Fields<Map<'Key, 'Value>, 'Value -> Map<'Key, 'Value>> =
                    {
                        Validation = fields.Validation
                        ReadMembers =
                            fun path value ->
                                applying.ReadMembers path value
                                |> Result.map (fun apply item -> apply (Some item))
                        WriteMembers = fields.WriteMembers
                        MemberShapes = fields.MemberShapes
                        MemberDefinitions = fields.MemberDefinitions
                        OptionalNames = fields.OptionalNames
                    }

                field name (Map.find key) codec required

        members |> List.fold add (record Map.empty) |> build

    /// Interpret structurally admitted fields through a semantic constructor; its rules do not alter Schema.
    let buildResult describe (fields: Fields<'Whole, Result<'Whole, 'Error>>) : Codec<'Whole> =
        {
            Validation = fields.Validation
            ReadMembers =
                fun path element ->
                    fields.ReadMembers path element |> Result.bind (Result.mapError (describe path))
            WriteMembers = fields.WriteMembers
            MemberShapes = fields.MemberShapes
            MemberDefinitions = fields.MemberDefinitions
            OptionalNames = fields.OptionalNames
        }
        |> build
