namespace ClaimCore.Cli

open ClaimCore.Contracts

open System
open System.Text
open System.Text.Json

module StrictJson =
    let utf8 = UTF8Encoding(false, true)

    let private failure reason path =
        Error(ProtocolFailure.create reason (ProtocolLocation.fromPath path))

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
                                ProtocolProblem.DuplicateProperty
                                (ProtocolLocation.fromPath path)
                        )
                    else
                        names <- Set.add property.Name names
                        visit path property.Value)
            | JsonValueKind.Array ->
                element.EnumerateArray()
                |> Seq.map (fun item -> visit path item)
                |> Seq.tryPick id
            | _ -> None

        visit "" root

    let parseDocument maximumBytes (bytes: byte array) =
        if bytes.Length > maximumBytes then
            failure ProtocolProblem.DocumentTooLarge ""
        elif bytes.Length >= 3 && bytes[0] = 0xEFuy && bytes[1] = 0xBBuy && bytes[2] = 0xBFuy then
            failure ProtocolProblem.ByteOrderMark ""
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
            | :? DecoderFallbackException -> failure ProtocolProblem.InvalidUtf8 ""
            | :? InvalidOperationException -> failure ProtocolProblem.InvalidUnicode ""
            | :? JsonException -> failure ProtocolProblem.InvalidJson ""

    let objectAt path (value: JsonElement) =
        if value.ValueKind = JsonValueKind.Object then
            Ok value
        else
            failure ProtocolProblem.ExpectedObject path

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
            | Some _ -> failure ProtocolProblem.UnknownProperty path
            | None ->
                let missing = expected |> List.tryFind (fun name -> not (Set.contains name names))

                match missing with
                | Some name -> failure ProtocolProblem.MissingProperty (path + "/" + name)
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
            | Some _ -> failure ProtocolProblem.UnknownProperty path
            | None ->
                match required |> List.tryFind (fun name -> not (Set.contains name names)) with
                | Some name -> failure ProtocolProblem.MissingProperty (path + "/" + name)
                | None -> Ok source

    let requiredProperty path (name: string) (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.Object when value.TryGetProperty(name) |> fst -> Ok(value.GetProperty(name))
        | JsonValueKind.Object -> failure ProtocolProblem.MissingProperty (path + "/" + name)
        | _ -> failure ProtocolProblem.ExpectedObject path

    let optionalProperty (name: string) (value: JsonElement) =
        match value.TryGetProperty(name) with
        | true, property -> Some property
        | false, _ -> None

    let stringAt path (value: JsonElement) =
        if value.ValueKind = JsonValueKind.String then
            try
                value.GetString() |> Option.ofObj |> Option.defaultValue "" |> Ok
            with :? InvalidOperationException ->
                failure ProtocolProblem.InvalidUnicode path
        else
            failure ProtocolProblem.ExpectedString path

    let boolAt path (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.True -> Ok true
        | JsonValueKind.False -> Ok false
        | _ -> failure ProtocolProblem.ExpectedBoolean path

    let integerAt path minimum maximum (value: JsonElement) =
        if value.ValueKind <> JsonValueKind.Number then
            failure ProtocolProblem.ExpectedNumber path
        else
            match value.TryGetInt32() with
            | true, number when number >= minimum && number <= maximum -> Ok number
            | _ -> failure ProtocolProblem.IntegerRange path

    let oneOf path allowed value =
        if List.contains value allowed then
            Ok value
        else
            failure ProtocolProblem.InvalidToken path

    let typeName (value: JsonElement) = valueKindName value.ValueKind
