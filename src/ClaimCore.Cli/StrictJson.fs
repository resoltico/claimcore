namespace ClaimCore.Cli

open System
open System.Text
open System.Text.Json

module StrictJson =
    let utf8 = UTF8Encoding(false, true)

    let private failure code message path =
        Error(ProtocolFailure.create code message path)

    let private valueKindName kind =
        match kind with
        | JsonValueKind.Array -> "array"
        | JsonValueKind.False
        | JsonValueKind.True -> "boolean"
        | JsonValueKind.Null -> "null"
        | JsonValueKind.Number -> "number"
        | JsonValueKind.Object -> "object"
        | JsonValueKind.String -> "string"
        | _ -> "invalid JSON"

    let private checkDuplicates root =
        let rec visit path (element: JsonElement) =
            match element.ValueKind with
            | JsonValueKind.Object ->
                let mutable names = Set.empty
                let properties = element.EnumerateObject() |> Seq.toList

                properties
                |> List.tryPick (fun property ->
                    if Set.contains property.Name names then
                        Some(
                            ProtocolFailure.create
                                "DUPLICATE_KEY"
                                "JSON object keys must be unique."
                                path
                        )
                    else
                        names <- Set.add property.Name names
                        visit path property.Value)
            | JsonValueKind.Array ->
                element.EnumerateArray()
                |> Seq.mapi (fun index item -> visit (path + "/" + string index) item)
                |> Seq.tryPick id
            | _ -> None

        visit "" root

    let parseDocument maximumBytes (bytes: byte array) =
        if bytes.Length > maximumBytes then
            failure "INPUT_TOO_LARGE" "The JSON input exceeds the configured byte limit." ""
        elif bytes.Length >= 3 && bytes[0] = 0xEFuy && bytes[1] = 0xBBuy && bytes[2] = 0xBFuy then
            failure "UTF8_BOM_FORBIDDEN" "UTF-8 input must not start with a byte-order mark." ""
        else
            try
                utf8.GetString(bytes) |> ignore

                let options =
                    JsonDocumentOptions(
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth = 64
                    )

                let document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes), options)

                let duplicate =
                    try
                        checkDuplicates document.RootElement
                    with _ ->
                        document.Dispose()
                        reraise ()

                match duplicate with
                | Some problem ->
                    document.Dispose()
                    Error problem
                | None -> Ok document
            with
            | :? DecoderFallbackException ->
                failure "INVALID_UTF8" "JSON input must be valid UTF-8." ""
            | :? InvalidOperationException ->
                failure "INVALID_UNICODE" "JSON text must contain valid Unicode scalars." ""
            | :? JsonException ->
                failure "INVALID_JSON" "Input must be one strict JSON document." ""

    let objectAt path (value: JsonElement) =
        if value.ValueKind = JsonValueKind.Object then
            Ok value
        else
            failure "INVALID_SHAPE" "Expected a JSON object." path

    let exactProperties path expected (value: JsonElement) =
        match objectAt path value with
        | Error problem -> Error problem
        | Ok source ->
            let properties = source.EnumerateObject() |> Seq.toList
            let names = properties |> List.map _.Name |> Set.ofList
            let expectedNames = expected |> Set.ofList

            match
                properties
                |> List.tryFind (fun property -> not (Set.contains property.Name expectedNames))
            with
            | Some _ ->
                failure "UNKNOWN_PROPERTY" "The JSON object contains an unknown property." path
            | None ->
                let missing = expected |> List.tryFind (fun name -> not (Set.contains name names))

                match missing with
                | Some name ->
                    failure
                        "MISSING_PROPERTY"
                        "The JSON object is missing a required property."
                        (path + "/" + name)
                | None -> Ok source

    let allowedProperties path required allowed (value: JsonElement) =
        match objectAt path value with
        | Error problem -> Error problem
        | Ok source ->
            let properties = source.EnumerateObject() |> Seq.toList
            let names = properties |> List.map _.Name |> Set.ofList
            let allowedNames = allowed |> Set.ofList

            match
                properties
                |> List.tryFind (fun property -> not (Set.contains property.Name allowedNames))
            with
            | Some _ ->
                failure "UNKNOWN_PROPERTY" "The JSON object contains an unknown property." path
            | None ->
                match required |> List.tryFind (fun name -> not (Set.contains name names)) with
                | Some name ->
                    failure
                        "MISSING_PROPERTY"
                        "The JSON object is missing a required property."
                        (path + "/" + name)
                | None -> Ok source

    let requiredProperty path (name: string) (value: JsonElement) =
        match value.TryGetProperty(name) with
        | true, property -> Ok property
        | false, _ ->
            failure
                "MISSING_PROPERTY"
                "The JSON object is missing a required property."
                (path + "/" + name)

    let optionalProperty (name: string) (value: JsonElement) =
        match value.TryGetProperty(name) with
        | true, property -> Some property
        | false, _ -> None

    let stringAt path (value: JsonElement) =
        if value.ValueKind = JsonValueKind.String then
            try
                value.GetString() |> Option.ofObj |> Option.defaultValue "" |> Ok
            with :? InvalidOperationException ->
                failure "INVALID_UNICODE" "JSON text must contain valid Unicode scalars." path
        else
            failure "INVALID_SHAPE" "Expected a JSON string." path

    let boolAt path (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.True -> Ok true
        | JsonValueKind.False -> Ok false
        | _ -> failure "INVALID_SHAPE" "Expected a JSON boolean." path

    let integerAt path minimum maximum (value: JsonElement) =
        match value.ValueKind, value.TryGetInt32() with
        | JsonValueKind.Number, (true, number) when number >= minimum && number <= maximum ->
            Ok number
        | JsonValueKind.Number, _ ->
            failure "INVALID_RANGE" "The number is outside its permitted range." path
        | _ -> failure "INVALID_SHAPE" "Expected a JSON number." path

    let oneOf path allowed value =
        if List.contains value allowed then
            Ok value
        else
            failure "INVALID_VALUE" "The value is not one of the permitted tokens." path

    let typeName (value: JsonElement) = valueKindName value.ValueKind
