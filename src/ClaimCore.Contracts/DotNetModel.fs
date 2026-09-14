namespace ClaimCore.Contracts

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.RegularExpressions

/// Only the schema shapes published by this product are supported. No object/any fallback.
module internal DotNetModel =
    type Node = { Name: string; Schema: Schema }

    type Model =
        {
            Nodes: Node list
            Aliases: Map<string, string>
            Endpoints: WebEndpoint list
            Scalars: Node list
            ScalarNames: Map<Schema, string>
        }

    let resolve (model: Model) name =
        Map.tryFind name model.Aliases |> Option.defaultValue name

    let literal (value: string) = JsonSerializer.Serialize(value)

    let option render value =
        value
        |> Option.map (fun x -> "(Some " + render x + ")")
        |> Option.defaultValue "None"

    let strings values =
        "[ " + (values |> List.map literal |> String.concat "; ") + " ]"

    let identifier (value: string) =
        let parts = Regex.Split(value, "[._-]")

        let result =
            parts
            |> Array.map (fun part ->
                if part.Length = 0 then
                    invalidOp "Protocol identifiers cannot have empty segments."

                if part = part.ToUpperInvariant() then
                    part.Substring(0, 1) + part.Substring(1).ToLowerInvariant()
                else
                    part.Substring(0, 1).ToUpperInvariant() + part.Substring(1))
            |> String.concat ""

        if not (Regex.IsMatch(result, "^[A-Za-z][A-Za-z0-9]*$")) then
            invalidOp "Protocol identifier cannot be represented in F#."

        result

    let nullable schema =
        match schema with
        | OneOfSchema [ value; NullSchema ]
        | OneOfSchema [ NullSchema; value ] -> Some value
        | _ -> None

    let private textConstant name schema =
        match schema with
        | ObjectSchema shape ->
            shape.Properties
            |> List.tryPick (fun p ->
                match p.Required, p.Name, p.Schema with
                | true, key, ConstantSchema(TextConstant value) when key = name -> Some value
                | _ -> None)
        | _ -> None

    let alternatives schemas =
        if List.isEmpty schemas then
            invalidOp "Protocol oneOf cannot be empty."

        let choose key =
            let values = schemas |> List.choose (textConstant key)

            if values.Length = schemas.Length && (Set.ofList values).Count = values.Length then
                Some(key, List.zip values schemas)
            else
                None

        [ "tag"; "kind"; "name"; "prefill" ]
        |> List.tryPick choose
        |> Option.defaultWith (fun () ->
            invalidOp "Protocol oneOf requires unique, required text discriminators.")

    let private requireTextEnum values =
        let textOnly =
            values
            |> List.forall (function
                | TextConstant _ -> true
                | _ -> false)

        if values.IsEmpty || not textOnly then
            invalidOp "Protocol enumerations must be nonempty text sets."

    let private rejectScalar schema =
        match schema with
        | NeverSchema -> invalidOp "A never schema has no usable protocol binding."
        | EnumerationSchema values -> requireTextEnum values
        | StringSchema constraints ->
            if
                not (
                    List.contains
                        constraints.Format
                        [ None; Some "date"; Some "date-time"; Some "uuid" ]
                )
            then
                invalidOp "Unsupported protocol scalar format."
        | _ -> ()

    let scalarType schema =
        match schema with
        | StringSchema _
        | EnumerationSchema _ -> Some "string"
        | IntegerSchema _ -> Some "int64"
        | BooleanSchema -> Some "bool"
        | NullSchema -> Some "unit"
        | ConstantSchema value ->
            match value with
            | TextConstant _ -> Some "string"
            | IntegerConstant _ -> Some "int64"
            | BooleanConstant _ -> Some "bool"
            | NullConstant -> Some "unit"
        | _ -> None

    let build (projection: ContractModel) =
        let definitions =
            WebSchemaDefinitions.all projection.Semantic projection.DefinitionSchema.Root
            @ WebTypeScript.semanticAliases projection
            |> Map.ofList

        let nodes = ResizeArray<Node>()
        let seen = Dictionary<string, Schema>(StringComparer.Ordinal)
        let aliases = Dictionary<string, string>(StringComparer.Ordinal)
        let schemas = Dictionary<Schema, string>()
        let visiting = HashSet<string>(StringComparer.Ordinal)
        let scalars = ResizeArray<Node>()
        let scalarNames = Dictionary<Schema, string>()

        let scalar schema =
            if not (scalarNames.ContainsKey(schema)) then
                let name = "ProtocolScalar" + string scalars.Count
                scalarNames.Add(schema, name)
                scalars.Add({ Name = name; Schema = schema })

        let rec visit name schema =
            rejectScalar schema

            match schema with
            | ReferenceSchema key ->
                match Map.tryFind key definitions with
                | Some value -> named key value
                | None -> invalidOp ("Unresolved protocol reference: " + key)
            | OneOfSchema [ value ] -> visit name value
            | OneOfSchema _ when (nullable schema).IsSome ->
                visit name (nullable schema |> Option.get)
            | ArraySchema value -> visit (name + "Item") value.Item
            | DictionarySchema value -> visit (name + "Value") value
            | ObjectSchema _
            | OneOfSchema _
            | TupleSchema _ -> named name schema
            | _ -> scalar schema

        and named name schema =
            if visiting.Contains(name) then
                invalidOp "Recursive protocol schemas require an explicit supported design."

            match seen.TryGetValue name with
            | true, existing when existing = schema -> ()
            | true, _ -> invalidOp ("Conflicting generated protocol name: " + name)
            | _ ->
                match schemas.TryGetValue(schema) with
                | true, canonical ->
                    seen.Add(name, schema)
                    aliases.Add(name, canonical)
                | _ ->
                    visiting.Add(name) |> ignore
                    children name schema
                    visiting.Remove(name) |> ignore
                    seen.Add(name, schema)
                    aliases.Add(name, name)
                    schemas.Add(schema, name)
                    nodes.Add({ Name = name; Schema = schema })

        and children name schema =
            match schema with
            | ObjectSchema value ->
                if value.AdditionalProperties then
                    invalidOp "Open protocol objects require an explicit typed representation."

                let fields = value.Properties |> List.map (fun p -> identifier p.Name)

                if fields.Length <> (Set.ofList fields).Count then
                    invalidOp "Protocol member names collide."

                value.Properties
                |> List.iter (fun p -> visit (name + identifier p.Name) p.Schema)
            | OneOfSchema values ->
                let _, choices = alternatives values
                let labels = choices |> List.map (fst >> identifier)

                if labels.Length <> (Set.ofList labels).Count then
                    invalidOp "Protocol alternative names collide."

                choices |> List.iter (fun (tag, value) -> visit (name + identifier tag) value)
            | TupleSchema values ->
                values |> List.iteri (fun i value -> visit (name + "Item" + string i) value)
            | _ -> visit name schema

        definitions |> Map.iter (fun name schema -> visit name schema)

        let endpointNames =
            projection.WebEndpoints |> List.map (fun item -> identifier item.Identifier)

        if endpointNames.Length <> (Set.ofList endpointNames).Count then
            invalidOp "Protocol endpoint names collide."

        for endpoint in projection.WebEndpoints do
            let name = identifier endpoint.Identifier
            visit (name + "Response") endpoint.Response

            match endpoint.Body with
            | Some(JsonBody schema) -> visit (name + "Request") schema
            | Some(RawBody(_, _, headers)) ->
                visit
                    (name + "Headers")
                    (Schema.objectOf
                        false
                        (headers |> List.map (fun (key, schema) -> Schema.property key schema true)))
            | None -> ()

        {
            Nodes = List.ofSeq nodes
            Aliases = aliases |> Seq.map (fun p -> p.Key, p.Value) |> Map.ofSeq
            Endpoints = projection.WebEndpoints
            Scalars = List.ofSeq scalars
            ScalarNames = scalarNames |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq
        }

    let rec typeName model name schema =
        match scalarType schema with
        | Some value -> value
        | None ->
            match schema with
            | ReferenceSchema key -> resolve model key
            | OneOfSchema [ value ] -> typeName model name value
            | OneOfSchema _ when (nullable schema).IsSome ->
                "(" + typeName model name (nullable schema |> Option.get) + ") option"
            | ArraySchema value -> "(" + typeName model (name + "Item") value.Item + ") list"
            | DictionarySchema value -> "Map<string, " + typeName model (name + "Value") value + ">"
            | ObjectSchema _
            | OneOfSchema _
            | TupleSchema _ -> resolve model name
            | _ -> invalidOp "Unsupported protocol type."
