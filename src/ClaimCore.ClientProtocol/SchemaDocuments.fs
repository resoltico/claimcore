namespace ClaimCore.ClientProtocol

open System.Text.Json

/// Reads generated structural documents; no semantic annotations cross the client boundary.
module SchemaDocuments =
    let private optional (name: string) (value: JsonElement) read =
        match value.TryGetProperty name with
        | true, item -> Some(read item)
        | _ -> None

    let private constant (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.String -> TextConstant(value.GetString() |> nonNull)
        | JsonValueKind.Number -> IntegerConstant(value.GetInt64())
        | JsonValueKind.True -> BooleanConstant true
        | JsonValueKind.False -> BooleanConstant false
        | JsonValueKind.Null -> NullConstant
        | _ -> invalidArg "value" "Unsupported schema constant."

    let rec private read (value: JsonElement) =
        let values (name: string) =
            value.GetProperty(name).EnumerateArray() |> Seq.map read |> Seq.toList

        match value.ValueKind with
        | JsonValueKind.False -> Schema.never
        | _ ->
            match
                value.TryGetProperty "$ref",
                value.TryGetProperty "oneOf",
                value.TryGetProperty "const",
                value.TryGetProperty "enum"
            with
            | (true, reference), _, _, _ ->
                Schema.reference ((reference.GetString() |> nonNull).Substring(8))
            | _, (true, _), _, _ -> Schema.oneOf (values "oneOf")
            | _, _, (true, item), _ -> Schema.constant (constant item)
            | _, _, _, (true, items) ->
                items.EnumerateArray() |> Seq.map constant |> Seq.toList |> Schema.enumeration
            | _ -> typed value

    and private typed (value: JsonElement) =
        let text (item: JsonElement) = item.GetString() |> nonNull

        match value.GetProperty("type").GetString() with
        | "string" ->
            Schema.string
                (optional "format" value text)
                (optional "pattern" value text)
                (optional "minLength" value (fun (item: JsonElement) -> item.GetInt32()))
                (optional "maxLength" value (fun (item: JsonElement) -> item.GetInt32()))
        | "integer" ->
            Schema.integer
                (optional "minimum" value (fun (item: JsonElement) -> item.GetInt64()))
                (optional "maximum" value (fun (item: JsonElement) -> item.GetInt64()))
        | "boolean" -> Schema.boolean
        | "null" -> Schema.nullValue
        | "array" ->
            match value.TryGetProperty "prefixItems", value.TryGetProperty "items" with
            | (true, items), _ ->
                items.EnumerateArray() |> Seq.map read |> Seq.toList |> Schema.tuple
            | _, (true, items) when items.ValueKind = JsonValueKind.False -> Schema.tuple []
            | _, (true, items) ->
                Schema.array
                    (read items)
                    (optional "minItems" value (fun (item: JsonElement) -> item.GetInt32()))
                    (optional "maxItems" value (fun (item: JsonElement) -> item.GetInt32()))
            | _ -> invalidArg "value" "Unsupported array schema."
        | "object" -> objectSchema value
        | _ -> invalidArg "value" "Unsupported generated schema."

    and private objectSchema (value: JsonElement) =
        match value.TryGetProperty "properties" with
        | true, properties ->
            let required =
                optional "required" value (fun items ->
                    items.EnumerateArray()
                    |> Seq.map (fun item -> item.GetString() |> nonNull)
                    |> Set.ofSeq)
                |> Option.defaultValue Set.empty

            let members =
                properties.EnumerateObject()
                |> Seq.map (fun item ->
                    Schema.property item.Name (read item.Value) (Set.contains item.Name required))
                |> Seq.toList

            Schema.objectOf (value.GetProperty("additionalProperties").GetBoolean()) members
        | _ -> Schema.dictionary (read (value.GetProperty "additionalProperties"))

    let decode bytes =
        use document = JsonDocument.Parse(System.ReadOnlyMemory<byte> bytes)
        let root = document.RootElement

        let definitions =
            root.GetProperty("$defs").EnumerateObject()
            |> Seq.map (fun item -> item.Name, read item.Value)
            |> Seq.toList

        {
            Identifier = root.GetProperty("$id").GetString() |> nonNull
            Title = root.GetProperty("title").GetString() |> nonNull
            Root = Schema.reference "root"
            Definitions = definitions
        }
