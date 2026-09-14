namespace ClaimCore.Contracts

open System
open DotNetModel

module internal DotNetScalarExpressions =
    // JSON Schema regexes use Unicode scalars; .NET regexes use UTF-16. Original text is
    // checked for well-formed Unicode before matching, so the scalar surrogate guard is redundant.
    let private pattern (value: string) =
        value
            .Replace("(?![\\s\\S]*[\\ud800-\\udfff])", "", StringComparison.Ordinal)
            .Replace("^", "\\A", StringComparison.Ordinal)
            .Replace("$", "\\z", StringComparison.Ordinal)

    let private constant value =
        match value with
        | TextConstant text -> literal text, "text"
        | IntegerConstant number -> string number + "L", "integer None None"
        | BooleanConstant value -> (if value then "true" else "false"), "boolean"
        | NullConstant -> "()", "nullValue"

    let read schema =
        match schema with
        | StringSchema value ->
            "ScalarRead.stringValue "
            + option literal value.Format
            + " "
            + option (pattern >> literal) value.Pattern
            + " "
            + option string value.MinimumLength
            + " "
            + option string value.MaximumLength
        | IntegerSchema value ->
            "ScalarRead.integer "
            + option (fun n -> string n + "L") value.Minimum
            + " "
            + option (fun n -> string n + "L") value.Maximum
        | BooleanSchema -> "ScalarRead.boolean"
        | NullSchema -> "ScalarRead.nullValue"
        | ConstantSchema value ->
            let expected, reader = constant value

            "(fun item -> ScalarRead."
            + reader
            + " item |> ScalarRead.literal "
            + expected
            + ")"
        | EnumerationSchema values ->
            let values =
                values
                |> List.map (function
                    | TextConstant value -> value
                    | _ -> invalidOp "Non-text enum.")

            "ScalarRead.enumeration " + strings values
        | _ -> invalidOp "Unsupported scalar reader."

    let write schema =
        match schema with
        | StringSchema _
        | EnumerationSchema _ -> "JsonWrite.text"
        | IntegerSchema _ -> "JsonWrite.integer"
        | BooleanSchema -> "JsonWrite.boolean"
        | NullSchema -> "JsonWrite.nullValue"
        | ConstantSchema value ->
            let expected, reader = constant value

            let writer =
                if reader.StartsWith("integer", StringComparison.Ordinal) then
                    "integer"
                else
                    reader

            "JsonWrite.literal " + expected + " JsonWrite." + writer
        | _ -> invalidOp "Unsupported scalar writer."
