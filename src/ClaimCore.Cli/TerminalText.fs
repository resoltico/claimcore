namespace ClaimCore.Cli

open System.Globalization
open System.Text

/// Human data is escaped independently of trusted terminal layout and machine serialization.
module internal TerminalText =
    let private unsafeCharacter (character: char) =
        let point = int character
        let category = CharUnicodeInfo.GetUnicodeCategory character

        category = UnicodeCategory.Control
        || category = UnicodeCategory.LineSeparator
        || category = UnicodeCategory.ParagraphSeparator
        || point = 0x061C
        || point = 0x200E
        || point = 0x200F
        || (point >= 0x202A && point <= 0x202E)
        || (point >= 0x2066 && point <= 0x206F)

    /// Backslashes are doubled so a literal escape-like value remains distinguishable.
    let data (value: string) =
        let output = StringBuilder(value.Length)

        for character in value do
            if character = '\\' then
                output.Append("\\\\") |> ignore
            elif unsafeCharacter character then
                output
                    .Append("\\u{")
                    .Append((int character).ToString("X4", CultureInfo.InvariantCulture))
                    .Append('}')
                |> ignore
            else
                output.Append character |> ignore

        output.ToString()
