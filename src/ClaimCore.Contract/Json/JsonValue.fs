namespace ClaimCore.Contract

open System

/// The integer-only I-JSON value grammar. Object declaration order is retained.
[<RequireQualifiedAccess>]
type JsonValue =
    | Object of (string * JsonValue) list
    | Array of JsonValue list
    | String of string
    | Integer of int64
    | Boolean of bool
    | Null

module JsonValue =
    [<Literal>]
    let MaximumInteger = 9007199254740991L

    let tryMember name =
        function
        | JsonValue.Object members -> members |> List.tryFind (fst >> (=) name) |> Option.map snd
        | _ -> None

    let isText (value: string) =
        let mutable valid = not (obj.ReferenceEquals(value, null))
        let mutable index = 0

        while valid && index < value.Length do
            let c = value[index]

            if Char.IsHighSurrogate c then
                if index + 1 < value.Length && Char.IsLowSurrogate value[index + 1] then
                    let scalar = Char.ConvertToUtf32(c, value[index + 1])
                    valid <- (scalar &&& 0xFFFE) <> 0xFFFE
                    index <- index + 2
                else
                    valid <- false
            elif Char.IsLowSurrogate c then
                valid <- false
            else
                let scalar = int c
                valid <- not (scalar >= 0xFDD0 && scalar <= 0xFDEF) && (scalar &&& 0xFFFE) <> 0xFFFE
                index <- index + 1

        valid
