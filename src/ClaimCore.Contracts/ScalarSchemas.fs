namespace ClaimCore.Contracts

open System
open System.Globalization
open ClaimCore.Domain

[<RequireQualifiedAccess>]
module internal ScalarSchemas =
    let private boundedDecimalPattern (maximum: int64) =
        let digits = maximum.ToString(CultureInfo.InvariantCulture)
        let shorter = "0|[1-9][0-9]{0," + string (digits.Length - 2) + "}"

        let sameLength =
            [
                for index in 0 .. digits.Length - 1 do
                    let lower = if index = 0 then 1 else 0
                    let upper = int digits[index] - int '0' - 1

                    if lower <= upper then
                        let digit = if lower = upper then string lower else $"[{lower}-{upper}]"

                        let remaining = digits.Length - index - 1

                        let suffix = if remaining = 0 then "" else $"[0-9]{{{remaining}}}"

                        yield digits.Substring(0, index) + digit + suffix

                yield digits
            ]

        "^(?:" + String.concat "|" (shorter :: sameLength) + ")$"

    let decimalText =
        let maximum = Int64.MaxValue - 1L

        Schema.string
            None
            (Some(boundedDecimalPattern maximum))
            (Some 1)
            (Some(maximum.ToString(CultureInfo.InvariantCulture).Length))

    // These explicit ranges match Char.IsWhiteSpace/String.Trim for the supported Unicode scalar
    // values. C0/C1 controls and lone UTF-16 surrogates are separate Domain constraints.
    let private whitespace =
        "\\u0009-\\u000d\\u0020\\u0085\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000"

    let private textGuards (constraints: ScalarTextConstraints) =
        [
            if constraints.RequiresNonBlank then
                "(?![" + whitespace + "]*$)"

            if constraints.RejectsSurroundingWhitespace then
                "(?![" + whitespace + "])(?![\\s\\S]*[" + whitespace + "]$)"

            if constraints.RejectsControlCharacters then
                "(?![\\s\\S]*[\\u0000-\\u001f\\u007f-\\u009f])"

            if constraints.RequiresWellFormedUnicode then
                "(?![\\s\\S]*[\\ud800-\\udfff])"
        ]

    let private textPattern (constraints: ScalarTextConstraints) =
        let guards = textGuards constraints

        if guards.IsEmpty then
            None
        else
            Some("^" + String.concat "" guards + "[\\s\\S]*$")

    let private grammarPattern (constraints: ScalarTextConstraints) grammar =
        Some("^" + String.concat "" (textGuards constraints) + "(?:" + grammar + ")$")

    let uuid =
        Schema.string
            (Some "uuid")
            (Some
                "^(?!00000000-0000-0000-0000-000000000000$)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")
            (Some 36)
            (Some 36)

    let private text (constraints: ScalarTextConstraints) =
        Schema.string
            None
            (textPattern constraints)
            (Some constraints.MinimumCharacters)
            (Some constraints.MaximumCharacters)

    let scalar (value: ScalarRule) =
        match value with
        | ScalarRule.CalendarDate rule ->
            if
                rule.ExactFormat <> "yyyy-MM-dd"
                || rule.Minimum <> DateOnly.MinValue
                || rule.Maximum <> DateOnly.MaxValue
            then
                invalidOp "The calendar-date schema does not project the Domain's declared rule."

            Schema.string
                (Some "date")
                (Some "^(?!0000-)[0-9]{4}-[0-9]{2}-[0-9]{2}$")
                (Some 10)
                (Some 10)
        | ScalarRule.Text constraints -> text constraints
        | ScalarRule.Amount rule ->
            Schema.string
                None
                (grammarPattern rule.Text rule.Grammar)
                (Some rule.Text.MinimumCharacters)
                (Some rule.Text.MaximumCharacters)
        | ScalarRule.Currency rule ->
            Schema.string
                None
                (grammarPattern rule.Text rule.Grammar)
                (Some rule.Text.MinimumCharacters)
                (Some rule.Text.MaximumCharacters)
        | ScalarRule.CaseStatus rule ->
            rule.AllowedValues
            |> List.map (CaseStatuses.token >> TextConstant)
            |> Schema.enumeration
