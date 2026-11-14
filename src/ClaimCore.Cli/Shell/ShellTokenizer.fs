namespace ClaimCore.Cli

open System
open System.Text

/// Quotes and escapes only: metacharacters, substitutions and wildcards remain literal text.
module internal ShellTokenizer =
    [<RequireQualifiedAccess>]
    type private Quoting =
        | None
        | Single
        | Double

    [<NoEquality; NoComparison>]
    type private Words =
        {
            Current: StringBuilder
            Finished: ResizeArray<string>
            mutable Started: bool
        }

    let private append words character =
        words.Started <- true
        words.Current.Append(character: char) |> ignore

    let private finish words =
        if words.Started then
            words.Finished.Add(words.Current.ToString())
            words.Current.Clear() |> ignore
            words.Started <- false

    let private escapedInDouble character =
        character = '$' || character = '`' || character = '"' || character = '\\'

    let private step words quoting escaped character =
        if escaped then
            if quoting = Quoting.Double && not (escapedInDouble character) then
                append words '\\'

            append words character
            quoting, false
        else
            match quoting, character with
            | Quoting.Single, '\'' -> Quoting.None, false
            | Quoting.Single, value ->
                append words value
                quoting, false
            | Quoting.Double, '"' -> Quoting.None, false
            | _, '\\' -> quoting, true
            | Quoting.None, '\'' ->
                words.Started <- true
                Quoting.Single, false
            | Quoting.None, '"' ->
                words.Started <- true
                Quoting.Double, false
            | Quoting.None, (' ' | '\t' | '\r' | '\n') ->
                finish words
                quoting, false
            | _, value ->
                append words value
                quoting, false

    let split (line: string) =
        let words =
            {
                Current = StringBuilder()
                Finished = ResizeArray<string>()
                Started = false
            }

        let mutable quoting = Quoting.None
        let mutable escaped = false

        for character in line do
            let next, pending = step words quoting escaped character
            quoting <- next
            escaped <- pending

        if quoting <> Quoting.None || escaped then
            Error()
        else
            finish words
            Ok(List.ofSeq words.Finished)
