namespace ClaimCore.Json

module SchemaJson =
    let rec private references =
        function
        | Schema.Ref name -> [ name ]
        | Schema.Object(properties, _) -> properties |> List.collect (snd >> references)
        | Schema.Array(items, _, _) -> references items
        | Schema.OneOf(variants, discriminator) ->
            (variants |> List.collect references)
            @ (discriminator |> Option.map (snd >> List.map snd) |> Option.defaultValue [])
        | _ -> []

    /// Publish exactly the definitions reachable from the public roots, including discriminator targets.
    let reachable definitions roots =
        let rec visit seen =
            function
            | [] -> seen
            | name :: rest when Set.contains name seen -> visit seen rest
            | name :: rest ->
                match Map.tryFind name definitions with
                | None -> invalidOp ("Missing schema definition: " + name)
                | Some shape -> visit (Set.add name seen) (references shape @ rest)

        let names = roots |> List.collect references |> visit Set.empty
        definitions |> Map.filter (fun name _ -> Set.contains name names)

    let private text value = JsonValue.String value
    let private number value = JsonValue.Integer(int64 value)

    let private optional key encode =
        Option.map (fun value -> key, encode value) >> Option.toList

    let private stringFields pattern format minimum maximum =
        [ "type", text "string" ]
        @ optional "pattern" text pattern
        @ optional "format" text format
        @ optional "minLength" number minimum
        @ optional "maxLength" number maximum

    let private discriminator prefix (property, mapping) =
        JsonValue.Object
            [
                "propertyName", text property
                "mapping",
                JsonValue.Object(
                    mapping |> List.map (fun (token, name) -> token, text (prefix + name))
                )
            ]

    let rec value prefix =
        function
        | Schema.Object(properties, optional) ->
            JsonValue.Object
                [
                    "type", text "object"
                    "additionalProperties", JsonValue.Boolean false
                    "properties",
                    JsonValue.Object(
                        properties |> List.map (fun (key, item) -> key, value prefix item)
                    )
                    "required",
                    JsonValue.Array(
                        properties
                        |> List.choose (fun (key, _) ->
                            if List.contains key optional then None else Some(text key))
                    )
                ]
        | Schema.Array(items, minimum, maximum) ->
            JsonValue.Object
                [
                    "type", text "array"
                    "items", value prefix items
                    "minItems", number minimum
                    "maxItems", number maximum
                ]
        | Schema.String(pattern, format, minimum, maximum) ->
            JsonValue.Object(stringFields pattern format minimum maximum)
        | Schema.Integer(minimum, maximum) ->
            JsonValue.Object
                [
                    "type", text "integer"
                    "minimum", JsonValue.Integer minimum
                    "maximum", JsonValue.Integer maximum
                ]
        | Schema.Boolean -> JsonValue.Object [ "type", text "boolean" ]
        | Schema.Null -> JsonValue.Object [ "type", text "null" ]
        | Schema.Const token -> JsonValue.Object [ "const", text token ]
        | Schema.OneOf([ single ], None) -> value prefix single
        | Schema.OneOf(variants, None) when Schema.tryLiteralSet variants |> Option.isSome ->
            JsonValue.Object
                [
                    "enum",
                    JsonValue.Array(Schema.tryLiteralSet variants |> Option.get |> List.map text)
                ]
        | Schema.OneOf(variants, tag) ->
            JsonValue.Object(
                [ "oneOf", JsonValue.Array(variants |> List.map (value prefix)) ]
                @ optional "discriminator" (discriminator prefix) tag
            )
        | Schema.Ref name -> JsonValue.Object [ "$ref", text (prefix + name) ]
