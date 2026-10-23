namespace ClaimCore.Contract

open System
open System.Text

[<RequireQualifiedAccess>]
type TemplatePart =
    | Literal of string
    | Hole of string

type Template = private Template of TemplatePart list

/// ICU MessageFormat restricted to literal text and simple named arguments.
module Template =
    let private argumentName (name: string) =
        name.Length > 0
        && Char.IsAsciiLetter name[0]
        && name |> Seq.forall Char.IsAsciiLetterOrDigit
        && not (List.contains name [ "constructor"; "prototype"; "toString"; "valueOf" ])

    let private quote (text: string) index =
        index + 1 < text.Length && (text[index + 1] = '{' || text[index + 1] = '}')

    let private quoted (text: string) index (literal: StringBuilder) =
        let mutable position = index + 1
        let mutable reading = true

        while position < text.Length && reading do
            if text[position] <> '\'' then
                literal.Append text[position] |> ignore
                position <- position + 1
            elif position + 1 < text.Length && text[position + 1] = '\'' then
                literal.Append '\'' |> ignore
                position <- position + 2
            else
                position <- position + 1
                reading <- false

        position

    let private flush (literal: StringBuilder) parts =
        if literal.Length = 0 then
            parts
        else
            let value = TemplatePart.Literal(literal.ToString())
            literal.Clear() |> ignore
            value :: parts

    let private scan (text: string) =
        let literal = StringBuilder()

        let rec loop position parts =
            if position = text.Length then
                Ok(Template(List.rev (flush literal parts)))
            else
                match text[position] with
                | '\'' when position + 1 < text.Length && text[position + 1] = '\'' ->
                    literal.Append '\'' |> ignore
                    loop (position + 2) parts
                | '\'' when quote text position -> loop (quoted text position literal) parts
                | '{' ->
                    let closing = text.IndexOf('}', position + 1)

                    if closing < 0 then
                        Error "Unclosed message argument."
                    else
                        let name = text.Substring(position + 1, closing - position - 1)

                        if not (argumentName name) then
                            Error "Unsupported message argument."
                        else
                            loop (closing + 1) (TemplatePart.Hole name :: flush literal parts)
                | '}' -> Error "Unexpected closing message brace."
                | character ->
                    literal.Append character |> ignore
                    loop (position + 1) parts

        loop 0 []

    let private safe (text: string) =
        JsonValue.isText text
        && text
           |> Seq.forall (fun character ->
               (not (Char.IsControl character) || character = '\n' || character = '\t')
               && not (character >= '\u202a' && character <= '\u202e')
               && not (character >= '\u2066' && character <= '\u2069')
               && character <> '\u200e'
               && character <> '\u200f')

    let parse text =
        if String.IsNullOrWhiteSpace text || text.Length > 8000 || not (safe text) then
            Error "Invalid message text."
        else
            scan text

    let parts (Template parts) = parts

    let arguments template =
        parts template
        |> List.choose (function
            | TemplatePart.Hole name -> Some name
            | _ -> None)
        |> Set.ofList

    let render template supplied =
        if (supplied |> Map.keys |> Set.ofSeq) <> arguments template then
            invalidArg (nameof supplied) "Message arguments do not match the template."

        parts template
        |> List.map (function
            | TemplatePart.Literal text -> text
            | TemplatePart.Hole name -> Map.find name supplied)
        |> String.concat ""
