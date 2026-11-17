namespace ClaimCore.ContractGeneration

open System
open System.Text.RegularExpressions

/// The explicitly supported shared syntax, not a translation of arbitrary native regexes.
module BrowserPatterns =
    let validate (pattern: string) =
        let refuse () =
            invalidArg "pattern" "Unsupported browser scalar pattern."

        let mutable at = 0
        let length = pattern.Length

        let starts (value: string) =
            pattern.AsSpan(at).StartsWith(value.AsSpan(), StringComparison.Ordinal)

        let take () =
            if at >= length then
                refuse ()

            let value = pattern[at]
            at <- at + 1
            value

        let literal value = value >= ' ' && value <= '~'

        let escaped inClass =
            let value = take ()

            if inClass && value = 'p' then
                if not (starts "{Cc}" || starts "{Zs}" || starts "{Zl}" || starts "{Zp}") then
                    refuse ()

                at <- at + 4
            elif inClass && value = 'u' && starts "FEFF" then
                at <- at + 4
            elif ("^$\\.*+?()[]{}|".Contains value) || (inClass && value = '-') then
                ()
            else
                refuse ()

        let characterClass () =
            if at < length && pattern[at] = '^' then
                at <- at + 1

            let mutable count = 0
            let mutable previousLiteral = false

            while at < length && pattern[at] <> ']' do
                let value = take ()

                if value = '\\' then
                    escaped true
                    previousLiteral <- false
                elif value = '[' || not (literal value) then
                    refuse ()
                elif value = '-' && count > 0 && at < length && pattern[at] <> ']' then
                    if not previousLiteral then
                        refuse ()

                    let last = take ()

                    if last = '\\' || last = '[' || not (literal last) then
                        refuse ()

                    previousLiteral <- false
                else
                    previousLiteral <- true

                count <- count + 1

            if count = 0 || take () <> ']' then
                refuse ()

        let quantifier () =
            if at < length then
                match pattern[at] with
                | '?'
                | '*'
                | '+' -> at <- at + 1
                | '{' ->
                    at <- at + 1

                    let digits () =
                        let first = at

                        while at < length && Char.IsAsciiDigit pattern[at] do
                            at <- at + 1

                        at > first

                    if not (digits ()) then
                        refuse ()

                    if at < length && pattern[at] = ',' then
                        at <- at + 1
                        digits () |> ignore

                    if take () <> '}' then
                        refuse ()
                | _ -> ()

        let rec expression nested =
            while at < length && pattern[at] <> ')' do
                let mutable repeatable = true

                if starts "(?![\\s\\S])" then
                    at <- at + "(?![\\s\\S])".Length
                    repeatable <- false
                else
                    match take () with
                    | '|' -> repeatable <- false
                    | '^'
                    | '$' -> repeatable <- false
                    | '[' -> characterClass ()
                    | '\\' -> escaped false
                    | '(' ->
                        if at < length && pattern[at] = '?' then
                            at <- at + 1

                            match take () with
                            | ':' -> ()
                            | '='
                            | '!' -> repeatable <- false
                            | _ -> refuse ()

                        expression true
                    | '.'
                    | '*'
                    | '+'
                    | '?'
                    | '{'
                    | '}'
                    | ']' -> refuse ()
                    | value when literal value -> ()
                    | _ -> refuse ()

                if repeatable then
                    quantifier ()

            if nested && take () <> ')' then
                refuse ()

        expression false

        if at <> length then
            refuse ()
        // Validate ranges, quantifier bounds and grouping after restricting language features.
        try
            Regex(pattern, RegexOptions.CultureInvariant) |> ignore
        with :? ArgumentException ->
            refuse ()
