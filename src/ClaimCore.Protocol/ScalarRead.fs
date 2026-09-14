namespace ClaimCore.Protocol

open System
open System.Globalization
open System.Text.Json
open System.Text.RegularExpressions

module internal ScalarRead =
    let private require condition =
        if not condition then
            JsonInput.reject "INVALID_VALUE"

    let text (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.String then
            JsonInput.reject "INVALID_SHAPE"

        element.GetString() |> nonNull |> JsonInput.unicode

    let private formatValue format (value: string) =
        match format with
        | None -> ()
        | Some "uuid" -> Guid.TryParseExact(value, "D") |> fst |> require
        | Some "date" ->
            DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None
            )
            |> fst
            |> require
        | Some "date-time" ->
            DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None
            )
            |> fst
            |> require
        | Some _ -> invalidOp "The generated codec contains an unsupported string format."

    let stringValue format pattern minimum maximum element =
        let value = text element
        let count = value.EnumerateRunes() |> Seq.length
        minimum |> Option.iter (fun limit -> require (count >= limit))
        maximum |> Option.iter (fun limit -> require (count <= limit))

        pattern
        |> Option.iter (fun expression ->
            require (
                Regex.IsMatch(
                    value,
                    expression,
                    RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds 100.
                )
            ))

        formatValue format value
        value

    let private exactInteger (element: JsonElement) =
        match element.TryGetInt64() with
        | true, value -> value
        | _ ->
            match element.TryGetDecimal() with
            | true, value when
                Decimal.Truncate(value) = value
                && value >= decimal Int64.MinValue
                && value <= decimal Int64.MaxValue
                ->
                let candidate = int64 value
                // Decimal parsing may round. Compare against the original JSON number,
                // whose DeepEquals semantics are exact, before accepting the conversion.
                require (
                    JsonElement.DeepEquals(element, JsonSerializer.SerializeToElement(candidate))
                )

                candidate
            | _ -> JsonInput.reject "INVALID_VALUE"

    let integer minimum maximum (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Number then
            JsonInput.reject "INVALID_SHAPE"

        let value = exactInteger element
        minimum |> Option.iter (fun limit -> require (value >= limit))
        maximum |> Option.iter (fun limit -> require (value <= limit))
        value

    let boolean (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.True -> true
        | JsonValueKind.False -> false
        | _ -> JsonInput.reject "INVALID_SHAPE"

    let nullValue (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Null then
            JsonInput.reject "INVALID_SHAPE"

    let literal expected actual =
        require (actual = expected)
        actual

    let enumeration allowed element =
        let value = text element
        require (List.contains value allowed)
        value
