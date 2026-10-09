namespace ClaimCore.Contracts

open System
open System.Globalization
open System.Text.Json
open System.Text.RegularExpressions
open SchemaFailures

module internal SchemaPrimitiveValidation =
    let matchesPattern (pattern: string) (value: string) =
        try
            Regex.IsMatch(
                value,
                pattern,
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(200.)
            )
        with :? RegexMatchTimeoutException ->
            false

    let validFormat format value =
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
            // Explicit seconds and offset prevent the parser from supplying a host calendar
            // or local zone. Wire schemas separately require canonical UTC round-trip spelling.
            matchesPattern
                @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}[Tt][0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?([Zz]|[+-][0-9]{2}:[0-9]{2})\z"
                value
            && (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None)
                |> fst)
        | Some "uri" ->
            match Uri.TryCreate(value, UriKind.Absolute) with
            | true, _ -> true
            | _ -> false
        | _ -> false

    let constantMatches constant (element: JsonElement) =
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


    let private patternFailure path constraints value =
        let violation =
            constraints.DiagnosticGuards
            |> List.tryPick (fun (pattern, reason) ->
                if matchesPattern pattern value then None else Some reason)

        let cause =
            violation
            |> Option.orElseWith (fun () ->
                SchemaFailures.stringConstraint constraints.ScalarRule None None)

        fail
            path
            (cause
             |> Option.map SchemaProblem.ScalarViolation
             |> Option.defaultValue SchemaProblem.InvalidValue)

    let private stringFailure path constraints count value =
        let short =
            constraints.MinimumLength |> Option.filter (fun minimum -> count < minimum)

        let long =
            constraints.MaximumLength |> Option.filter (fun maximum -> count > maximum)

        let priorGuard =
            constraints.DiagnosticGuards
            |> List.tryPick (fun (pattern, violation) ->
                match violation with
                | ClaimCore.Domain.InputViolation.Text ClaimCore.Domain.TextViolation.ControlCharacters
                | ClaimCore.Domain.InputViolation.Amount _ -> None
                | _ when not (matchesPattern pattern value) -> Some violation
                | _ -> None)

        let lengthViolation =
            SchemaFailures.stringConstraint constraints.ScalarRule short long

        if priorGuard.IsSome then
            fail path (SchemaProblem.ScalarViolation priorGuard.Value)
        elif short.IsSome || long.IsSome then
            fail
                path
                (lengthViolation
                 |> Option.map SchemaProblem.ScalarViolation
                 |> Option.defaultValue SchemaProblem.InvalidValue)
        elif not (validFormat constraints.Format value) then
            let cause = SchemaFailures.stringConstraint constraints.ScalarRule None None

            fail
                path
                (cause
                 |> Option.map SchemaProblem.ScalarViolation
                 |> Option.defaultValue (
                     SchemaProblem.InvalidFormat(defaultArg constraints.Format "")
                 ))
        elif
            constraints.Pattern
            |> Option.forall (fun pattern -> matchesPattern pattern value)
        then
            Ok()
        else
            patternFailure path constraints value

    let private stringValue path constraints (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.String then
            fail path SchemaProblem.StringRequired
        else
            match element.GetString() |> Option.ofObj with
            | None -> fail path SchemaProblem.StringRequired
            | Some value ->
                stringFailure path constraints (value.EnumerateRunes() |> Seq.length) value

    let private integerValue path constraints (element: JsonElement) =
        let mutable value = 0L

        if element.ValueKind <> JsonValueKind.Number then
            fail path SchemaProblem.NumberRequired
        else
            require
                path
                SchemaProblem.IntegerRange
                (element.TryGetInt64(&value)
                 && (constraints.Minimum |> Option.forall (fun minimum -> value >= minimum))
                 && (constraints.Maximum |> Option.forall (fun maximum -> value <= maximum)))

    let check path schema (element: JsonElement) =
        match schema with
        | StringSchema constraints -> Some(stringValue path constraints element)
        | IntegerSchema constraints -> Some(integerValue path constraints element)
        | BooleanSchema ->
            Some(
                require
                    path
                    SchemaProblem.BooleanRequired
                    (element.ValueKind = JsonValueKind.True
                     || element.ValueKind = JsonValueKind.False)
            )
        | NullSchema ->
            Some(require path SchemaProblem.InvalidValue (element.ValueKind = JsonValueKind.Null))
        | NeverSchema -> Some(fail path SchemaProblem.InvalidValue)
        | ConstantSchema value ->
            Some(require path SchemaProblem.InvalidValue (constantMatches value element))
        | EnumerationSchema values ->
            Some(
                require
                    path
                    SchemaProblem.InvalidValue
                    (values |> List.exists (fun value -> constantMatches value element))
            )
        | _ -> None
