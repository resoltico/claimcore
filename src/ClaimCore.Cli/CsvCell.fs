namespace ClaimCore.Cli

module internal CsvCell =
    let neutralize (value: string) =
        let trigger character =
            match character with
            | '='
            | '+'
            | '-'
            | '@'
            | '\t'
            | '\r' -> true
            | _ -> false

        let remaining = value.TrimStart [| ' '; '\t' |]

        if
            (value.Length > 0 && trigger value[0])
            || (remaining.Length > 0 && trigger remaining[0])
        then
            "'" + value
        else
            value

    let format value =
        let safe = neutralize value

        if safe.IndexOfAny [| ','; '"'; '\r'; '\n' |] >= 0 then
            "\"" + safe.Replace("\"", "\"\"") + "\""
        else
            safe
