namespace ClaimCore.Contract

module SchemaJson =
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
        | Schema.OneOf(variants, tag) ->
            JsonValue.Object(
                [ "oneOf", JsonValue.Array(variants |> List.map (value prefix)) ]
                @ optional "discriminator" (discriminator prefix) tag
            )
        | Schema.Ref name -> JsonValue.Object [ "$ref", text (prefix + name) ]
