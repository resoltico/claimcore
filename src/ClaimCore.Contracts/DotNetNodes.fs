namespace ClaimCore.Contracts

open DotNetModel

module internal DotNetNodes =
    let private fieldType model parent (field: ObjectProperty) =
        let fieldType = typeName model (parent + identifier field.Name) field.Schema

        if field.Required then
            fieldType
        else
            "(" + fieldType + ") option"

    let declaration model node =
        let title = "type " + node.Name + " ="

        match node.Schema with
        | ObjectSchema fields when fields.Properties.IsEmpty -> [ title + " unit" ]
        | ObjectSchema fields ->
            [ "[<NoComparison>]"; title; "    {" ]
            @ (fields.Properties
               |> List.map (fun field ->
                   "        " + identifier field.Name + ": " + fieldType model node.Name field))
            @ [ "    }" ]
        | OneOfSchema values ->
            let _, choices = alternatives values

            [ "[<RequireQualifiedAccess; NoComparison>]"; title ]
            @ (choices
               |> List.map (fun (tag, schema) ->
                   "    | "
                   + identifier tag
                   + " of "
                   + typeName model (node.Name + identifier tag) schema))
        | TupleSchema values ->
            let types =
                values
                |> List.mapi (fun i schema -> typeName model (node.Name + "Item" + string i) schema)

            [ title + " " + (if types.IsEmpty then "unit" else String.concat " * " types) ]
        | _ -> invalidOp "Unsupported protocol declaration."

    let private readObject model node (fields: ObjectConstraints) =
        let prefix = [ "JsonRead.objectValue properties value" ]

        if fields.Properties.IsEmpty then
            prefix @ [ "()" ]
        else
            prefix
            @ [ "{" ]
            @ (fields.Properties
               |> List.map (fun field ->
                   let name = identifier field.Name
                   let presence = if field.Required then "required" else "optional"

                   "    "
                   + name
                   + " = JsonRead."
                   + presence
                   + " "
                   + literal field.Name
                   + " ("
                   + DotNetExpressions.read model (node.Name + name) field.Schema
                   + ") value"))
            @ [ "}" ]

    let private writeObject model node (fields: ObjectConstraints) =
        [ "writer.WriteStartObject()" ]
        @ (fields.Properties
           |> List.map (fun field ->
               let name = identifier field.Name
               let presence = if field.Required then "property" else "optional"

               "JsonWrite."
               + presence
               + " "
               + literal field.Name
               + " ("
               + DotNetExpressions.write model (node.Name + name) field.Schema
               + ") writer value."
               + name))
        @ [ "writer.WriteEndObject()" ]

    let private readUnion model node values =
        let key, choices = alternatives values

        [ "match JsonRead.tag " + literal key + " value with" ]
        @ (choices
           |> List.map (fun (tag, schema) ->
               let name = identifier tag

               "| "
               + literal tag
               + " -> "
               + node.Name
               + "."
               + name
               + " (("
               + DotNetExpressions.read model (node.Name + name) schema
               + ") value)"))
        @ [ "| _ -> JsonInput.reject \"INVALID_VALUE\"" ]

    let private writeUnion model node values =
        let _, choices = alternatives values

        [ "match value with" ]
        @ (choices
           |> List.map (fun (tag, schema) ->
               let name = identifier tag

               "| "
               + node.Name
               + "."
               + name
               + " item -> ("
               + DotNetExpressions.write model (node.Name + name) schema
               + ") writer item"))

    let private readTuple model node values =
        let count = string (List.length values)

        let expressions =
            values
            |> List.mapi (fun i schema ->
                "("
                + DotNetExpressions.read model (node.Name + "Item" + string i) schema
                + ") items["
                + string i
                + "]")

        [
            (if values = [] then "let _items" else "let items")
            + " = JsonRead.items (Some "
            + count
            + ") (Some "
            + count
            + ") value"
            "(" + String.concat ", " expressions + ")"
        ]

    let private writeTuple model node values =
        let variables = values |> List.mapi (fun i _ -> "item" + string i)

        (if variables.IsEmpty then
             []
         else
             [ "let " + String.concat ", " variables + " = value" ])
        @ [ "writer.WriteStartArray()" ]
        @ (values
           |> List.mapi (fun i schema ->
               "("
               + DotNetExpressions.write model (node.Name + "Item" + string i) schema
               + ") writer item"
               + string i))
        @ [ "writer.WriteEndArray()" ]

    let codec model node =
        let read, write =
            match node.Schema with
            | ObjectSchema fields -> readObject model node fields, writeObject model node fields
            | OneOfSchema values -> readUnion model node values, writeUnion model node values
            | TupleSchema values -> readTuple model node values, writeTuple model node values
            | _ -> invalidOp "Unsupported protocol codec."

        let writeValue =
            match node.Schema with
            | ObjectSchema fields when fields.Properties.IsEmpty -> "_value"
            | TupleSchema [] -> "_value"
            | _ -> "value"

        [ "module internal " + node.Name + "Json =" ]
        @ (match node.Schema with
           | ObjectSchema fields ->
               [
                   "    let private properties = " + strings (fields.Properties |> List.map _.Name)
               ]
           | _ -> [])
        @ [ "    let read (value: JsonElement) : " + node.Name + " =" ]
        @ ((if node.Name = "SemanticDefinition" then
                "JsonRead.singleton ProtocolDefinition.expected value" :: read
            else
                read)
           |> List.map (fun line -> "        " + line))
        @ [
            ""
            "    let write (writer: Utf8JsonWriter) ("
            + writeValue
            + ": "
            + node.Name
            + ") ="
        ]
        @ (write |> List.map (fun line -> "        " + line))
