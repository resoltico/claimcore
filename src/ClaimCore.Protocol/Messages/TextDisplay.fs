namespace ClaimCore.Protocol


open System.Collections.Generic
open System.Globalization
open System.Text

/// Display annotations never change scalar admission or the retained value.
module TextDisplay =
    type Warning =
        {
            CodePoints: string list
            Truncated: bool
        }

    let private categories =
        [
            UnicodeCategory.Control, "Cc"
            UnicodeCategory.Format, "Cf"
            UnicodeCategory.LineSeparator, "Zl"
            UnicodeCategory.ParagraphSeparator, "Zp"
        ]

    let categoryTokens = categories |> List.map snd
    let maximumCodePoints = 8

    let warning (value: string) =
        let seen = HashSet<int>()
        let points = ResizeArray<string>()
        let mutable scalars = value.EnumerateRunes()
        let mutable truncated = false

        while not truncated && scalars.MoveNext() do
            let scalar = scalars.Current
            let category = Rune.GetUnicodeCategory scalar

            if (categories |> List.exists (fst >> (=) category)) && seen.Add scalar.Value then
                if points.Count = maximumCodePoints then
                    truncated <- true
                else
                    points.Add("U+" + scalar.Value.ToString("X", CultureInfo.InvariantCulture))

        {
            CodePoints = List.ofSeq points
            Truncated = truncated
        }
