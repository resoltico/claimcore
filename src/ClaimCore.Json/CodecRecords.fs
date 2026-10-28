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

    let closed members path element =
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

    /// A closed object whose declared members share a value representation and retain typed keys.
    let fieldMap (members: ('Key * string * Codec<'Value>) list) : Codec<Map<'Key, 'Value>> =
        let add (fields: Fields<Map<'Key, 'Value>, Map<'Key, 'Value>>) (key, name, codec) =
            let applying: Fields<Map<'Key, 'Value>, 'Value -> Map<'Key, 'Value>> =
                {
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
