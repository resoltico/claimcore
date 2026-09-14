namespace ClaimCore.Contracts

open DotNetModel

module internal DotNetExpressions =
    let rec read model name schema =
        match Map.tryFind schema model.ScalarNames with
        | Some scalar -> scalar + ".read"
        | None ->
            match schema with
            | ReferenceSchema key -> resolve model key + "Json.read"
            | OneOfSchema [ value ] -> read model name value
            | OneOfSchema _ when (nullable schema).IsSome ->
                "JsonRead.nullable (" + read model name (nullable schema |> Option.get) + ")"
            | ArraySchema value ->
                "JsonRead.array "
                + option string value.MinimumItems
                + " "
                + option string value.MaximumItems
                + " ("
                + read model (name + "Item") value.Item
                + ")"
            | DictionarySchema value ->
                "JsonRead.dictionary (" + read model (name + "Value") value + ")"
            | ObjectSchema _
            | OneOfSchema _
            | TupleSchema _ -> resolve model name + "Json.read"
            | _ -> invalidOp "Unsupported protocol reader."

    let rec write model name schema =
        match Map.tryFind schema model.ScalarNames with
        | Some scalar -> scalar + ".write"
        | None ->
            match schema with
            | ReferenceSchema key -> resolve model key + "Json.write"
            | OneOfSchema [ value ] -> write model name value
            | OneOfSchema _ when (nullable schema).IsSome ->
                "JsonWrite.nullable (" + write model name (nullable schema |> Option.get) + ")"
            | ArraySchema value ->
                "JsonWrite.array (" + write model (name + "Item") value.Item + ")"
            | DictionarySchema value ->
                "JsonWrite.dictionary (" + write model (name + "Value") value + ")"
            | ObjectSchema _
            | OneOfSchema _
            | TupleSchema _ -> resolve model name + "Json.write"
            | _ -> invalidOp "Unsupported protocol writer."
