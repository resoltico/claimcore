namespace ClaimCore.Contracts

open System
open System.Globalization
open System.Text.Json
open System.Text.RegularExpressions

/// Bounded runtime verification of the same schema AST used to generate service response schemas.
/// A failed check carries no authored values into transport diagnostics.
module SchemaValueValidation =
    let private matchesPattern (pattern: string) (value: string) =
        try
            Regex.IsMatch(
                value,
                pattern,
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(200.)
            )
        with :? RegexMatchTimeoutException ->
            false

    let private validFormat format value =
        match format with
        | None -> true
        | Some "uuid" ->
            match Guid.TryParseExact(value, "D") with
            | true, parsed -> parsed <> Guid.Empty && parsed.ToString("D") = value
            | _ -> false
        | Some "date" ->
            let mutable date = Unchecked.defaultof<DateOnly>

            DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                &date
            )
        | Some "date-time" ->
            match
                DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind
                )
            with
            | true, _ -> true
            | _ -> false
        | Some "uri" ->
            match Uri.TryCreate(value, UriKind.Absolute) with
            | true, _ -> true
            | _ -> false
        | _ -> false

    let private stringMatches constraints (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.String then
            false
        else
            match element.GetString() |> Option.ofObj with
            | None -> false
            | Some value ->
                let count = value.EnumerateRunes() |> Seq.length

                constraints.MinimumLength |> Option.forall (fun minimum -> count >= minimum)
                && (constraints.MaximumLength |> Option.forall (fun maximum -> count <= maximum))
                && validFormat constraints.Format value
                && (constraints.Pattern
                    |> Option.forall (fun pattern -> matchesPattern pattern value))

    let private integerMatches constraints (element: JsonElement) =
        let mutable number = 0L

        element.ValueKind = JsonValueKind.Number
        && element.TryGetInt64(&number)
        && (constraints.Minimum |> Option.forall (fun minimum -> number >= minimum))
        && (constraints.Maximum |> Option.forall (fun maximum -> number <= maximum))

    let private constantMatches constant (element: JsonElement) =
        match constant with
        | TextConstant expected ->
            element.ValueKind = JsonValueKind.String && element.GetString() = expected
        | IntegerConstant expected ->
            let mutable value = 0L

            element.ValueKind = JsonValueKind.Number
            && element.TryGetInt64(&value)
            && value = expected
        | BooleanConstant expected ->
            element.ValueKind = JsonValueKind.True && expected
            || element.ValueKind = JsonValueKind.False && not expected
        | NullConstant -> element.ValueKind = JsonValueKind.Null

    let private properties (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            None
        else
            let values = element.EnumerateObject() |> Seq.toList
            let names = values |> List.map _.Name

            if names.Length <> (names |> Set.ofList |> Set.count) then
                None
            else
                Some values

    let private primitive schema element =
        match schema with
        | StringSchema constraints -> Some(stringMatches constraints element)
        | IntegerSchema constraints -> Some(integerMatches constraints element)
        | BooleanSchema ->
            Some(
                element.ValueKind = JsonValueKind.True
                || element.ValueKind = JsonValueKind.False
            )
        | NullSchema -> Some(element.ValueKind = JsonValueKind.Null)
        | NeverSchema -> Some false
        | ConstantSchema value -> Some(constantMatches value element)
        | EnumerationSchema values ->
            Some(values |> List.exists (fun value -> constantMatches value element))
        | _ -> None

    let rec private check definitions depth schema element =
        if depth > 64 then
            false
        else
            match primitive schema element with
            | Some answer -> answer
            | None -> checkComposite definitions depth schema element

    and private checkComposite definitions depth schema element =
        match schema with
        | ArraySchema constraints ->
            if element.ValueKind <> JsonValueKind.Array then
                false
            else
                let values = element.EnumerateArray() |> Seq.toList

                (constraints.MinimumItems
                 |> Option.forall (fun minimum -> values.Length >= minimum))
                && (constraints.MaximumItems
                    |> Option.forall (fun maximum -> values.Length <= maximum))
                && (values |> List.forall (check definitions (depth + 1) constraints.Item))
        | TupleSchema schemas ->
            element.ValueKind = JsonValueKind.Array
            && (let values = element.EnumerateArray() |> Seq.toList

                values.Length = schemas.Length
                && List.forall2 (check definitions (depth + 1)) schemas values)
        | ObjectSchema constraints -> checkObject definitions depth constraints element
        | DictionarySchema value ->
            properties element
            |> Option.exists (
                List.forall (fun item -> check definitions (depth + 1) value item.Value)
            )
        | OneOfSchema alternatives ->
            alternatives
            |> List.filter (fun alternative -> check definitions (depth + 1) alternative element)
            |> List.length
            |> (=) 1
        | ReferenceSchema name ->
            definitions
            |> Map.tryFind name
            |> Option.exists (fun target -> check definitions (depth + 1) target element)
        | _ -> false

    and private checkObject definitions depth constraints element =
        match properties element with
        | None -> false
        | Some values ->
            let supplied = values |> List.map (fun item -> item.Name, item.Value) |> Map.ofList
            let declared = constraints.Properties |> List.map _.Name |> Set.ofList

            (constraints.AdditionalProperties
             || (supplied |> Map.forall (fun name _ -> Set.contains name declared)))
            && (constraints.Properties
                |> List.forall (fun item ->
                    match Map.tryFind item.Name supplied with
                    | None -> not item.Required
                    | Some value -> check definitions (depth + 1) item.Schema value))

    let verify (document: SchemaDocument) (element: JsonElement) =
        let definitions = document.Definitions |> Map.ofList

        try
            check definitions 0 document.Root element
        with :? InvalidOperationException ->
            false
