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
            ReadFields = fun _ _ -> Ok ctor
            Validate = fun _ _ -> Ok()
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
            Validate =
                fun path element ->
                    fields.Validate path element
                    |> Result.bind (fun () ->
                        match JsonValue.tryMember name element with
                        | Some value -> codec.Validate (path + "." + name) value
                        | None -> fail path ("missing member " + name))
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
            Validate =
                fun path element ->
                    fields.Validate path element
                    |> Result.bind (fun () ->
                        match JsonValue.tryMember name element with
                        | None -> Ok()
                        | Some value -> codec.Validate (path + "." + name) value)
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

    let closed members path element =
        match element with
        | JsonValue.Object values when
            values |> List.forall (fun (name, _) -> List.contains name members)
            && (values |> List.map fst |> List.distinct |> List.length) = values.Length
            ->
            Ok()
        | _ -> fail path "expected a closed object"

    let build (fields: Fields<'Whole, 'Whole>) : Codec<'Whole> =
        let members = fields.Properties |> List.map fst

        {
            Validate =
                fun path value ->
                    closed members path value |> Result.bind (fun () -> fields.Validate path value)
            Read =
                fun path element ->
                    closed members path element
                    |> Result.bind (fun () -> fields.ReadFields path element)
            Write = fun value -> JsonValue.Object(fields.WriteFields value)
            Schema = Schema.Object(fields.Properties, fields.OptionalMembers)
            Definitions = fields.PropertyDefinitions
        }

    /// Compose independently meaningful input groups in one closed wire object.
    let flatten
        get
        (part: Fields<'Part, 'Part>)
        (fields: Fields<'Whole, 'Part -> 'Rest>)
        : Fields<'Whole, 'Rest> =
        let repeated =
            Set.intersect
                (fields.Properties |> List.map fst |> Set.ofList)
                (part.Properties |> List.map fst |> Set.ofList)

        if not repeated.IsEmpty then
            invalidArg (nameof part) "Flattened object members must be disjoint."

        {
            Validate =
                fun path value ->
                    fields.Validate path value |> Result.bind (fun () -> part.Validate path value)
            ReadFields =
                fun path value ->
                    fields.ReadFields path value
                    |> Result.bind (fun apply -> part.ReadFields path value |> Result.map apply)
            WriteFields = fun value -> fields.WriteFields value @ part.WriteFields(get value)
            Properties = fields.Properties @ part.Properties
            PropertyDefinitions =
                SchemaDefinitions.merge fields.PropertyDefinitions part.PropertyDefinitions
            OptionalMembers = fields.OptionalMembers @ part.OptionalMembers
        }

    /// A closed object whose declared members share a value representation and retain typed keys.
    let fieldMap (members: ('Key * string * Codec<'Value>) list) : Codec<Map<'Key, 'Value>> =
        let add (fields: Fields<Map<'Key, 'Value>, Map<'Key, 'Value>>) (key, name, codec) =
            let applying: Fields<Map<'Key, 'Value>, 'Value -> Map<'Key, 'Value>> =
                {
                    Validate = fields.Validate
                    ReadFields =
                        fun path value ->
                            fields.ReadFields path value
                            |> Result.map (fun values item -> Map.add key item values)
                    WriteFields = fields.WriteFields
                    Properties = fields.Properties
                    PropertyDefinitions = fields.PropertyDefinitions
                    OptionalMembers = fields.OptionalMembers
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
                    Validate = fields.Validate
                    ReadFields =
                        fun path value ->
                            fields.ReadFields path value
                            |> Result.map (fun values item ->
                                match item with
                                | Some item -> Map.add key item values
                                | None -> values)
                    WriteFields = fields.WriteFields
                    Properties = fields.Properties
                    PropertyDefinitions = fields.PropertyDefinitions
                    OptionalMembers = fields.OptionalMembers
                }

            if optional then
                optionalField name (Map.tryFind key) codec applying
            else
                let required: Fields<Map<'Key, 'Value>, 'Value -> Map<'Key, 'Value>> =
                    {
                        Validate = fields.Validate
                        ReadFields =
                            fun path value ->
                                applying.ReadFields path value
                                |> Result.map (fun apply item -> apply (Some item))
                        WriteFields = fields.WriteFields
                        Properties = fields.Properties
                        PropertyDefinitions = fields.PropertyDefinitions
                        OptionalMembers = fields.OptionalMembers
                    }

                field name (Map.find key) codec required

        members |> List.fold add (record Map.empty) |> build

    /// Interpret structurally admitted fields through a semantic constructor; its rules do not alter Schema.
    let buildResult describe (fields: Fields<'Whole, Result<'Whole, 'Error>>) : Codec<'Whole> =
        {
            Validate = fields.Validate
            ReadFields =
                fun path element ->
                    fields.ReadFields path element |> Result.bind (Result.mapError (describe path))
            WriteFields = fields.WriteFields
            Properties = fields.Properties
            PropertyDefinitions = fields.PropertyDefinitions
            OptionalMembers = fields.OptionalMembers
        }
        |> build
