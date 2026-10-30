namespace ClaimCore.Domain

open Microsoft.FSharp.Reflection
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
type TokenCasing =
    | ScreamingSnake
    | CamelCase

[<NoEquality; NoComparison>]
type Vocabulary<'T when 'T: equality> =
    private
        {
            Name: string
            All: 'T list
            Token: 'T -> string
        }

module Vocabulary =
    let private representation =
        System.Reflection.BindingFlags.Public
        ||| System.Reflection.BindingFlags.NonPublic

    let formatToken casing prefix (name: string) =
        let words =
            Regex.Replace(name, "([A-Z]+)([A-Z][a-z])", "$1_$2")
            |> fun value -> Regex.Replace(value, "([a-z0-9])([A-Z])", "$1_$2")
            |> fun value -> value.Split('_') |> Array.toList

        let token =
            match casing, words with
            | TokenCasing.ScreamingSnake, _ -> String.concat "_" words |> _.ToUpperInvariant()
            | TokenCasing.CamelCase, first :: rest ->
                first.ToLowerInvariant()
                + (rest
                   |> List.map (fun word ->
                       word.Substring(0, 1).ToUpperInvariant()
                       + word.Substring(1).ToLowerInvariant())
                   |> String.concat "")
            | TokenCasing.CamelCase, [] -> ""

        Option.defaultValue "" prefix + token

    let requireUniqueTokens name tokens =
        if List.distinct tokens <> tokens then
            invalidArg name "Duplicate token."

        tokens

    let define<'T when 'T: equality> name (all: 'T list) (token: 'T -> string) : Vocabulary<'T> =
        if List.distinct all <> all then
            invalidArg name "Duplicate case."

        let tokens = List.map token all

        requireUniqueTokens name tokens |> ignore

        if FSharpType.IsUnion(typeof<'T>, representation) then
            let expected =
                FSharpType.GetUnionCases(typeof<'T>, representation)
                |> Array.map _.Tag
                |> Set.ofArray

            let actual =
                all
                |> List.map (fun value ->
                    let case, _ = FSharpValue.GetUnionFields(value, typeof<'T>, representation)
                    case.Tag)
                |> Set.ofList

            if actual <> expected then
                invalidArg name "Every union variant needs a token."

        {
            Name = name
            All = all
            Token = token
        }

    let ofUnion<'T when 'T: equality> name casing prefix : Vocabulary<'T> =
        if not (FSharpType.IsUnion(typeof<'T>, representation)) then
            invalidArg name "A vocabulary requires a nullary union."

        let cases = FSharpType.GetUnionCases(typeof<'T>, representation)

        if cases |> Array.exists (fun case -> case.GetFields().Length <> 0) then
            invalidArg name "A vocabulary requires a nullary union."

        let values =
            cases
            |> Array.map (fun case ->
                (FSharpValue.MakeUnion(case, [||], representation) |> nonNull) :?> 'T)
            |> Array.toList

        let readTag = FSharpValue.PreComputeUnionTagReader(typeof<'T>, representation)
        let tokens = cases |> Array.map (fun case -> formatToken casing prefix case.Name)

        define name values (fun value -> tokens[readTag (box value)])

    /// Compose already-declared sets, rejecting collisions in their shared token namespace.
    let ofEntries name (entries: ('T * string) list) : Vocabulary<'T> =
        define name (List.map fst entries) (fun value ->
            entries
            |> List.tryFind (fun (candidate, _) -> candidate = value)
            |> Option.map snd
            |> Option.defaultWith (fun () -> invalidArg name "Value is outside the vocabulary."))

    let name vocabulary = vocabulary.Name
    let all vocabulary = vocabulary.All
    let token vocabulary = vocabulary.Token

    let entries vocabulary =
        vocabulary.All |> List.map (fun value -> value, vocabulary.Token value)

    let tokens vocabulary =
        vocabulary.All |> List.map vocabulary.Token

    let tryParse vocabulary text =
        vocabulary.All |> List.tryFind (fun value -> vocabulary.Token value = text)

    let messageKey vocabulary value part =
        vocabulary.Name + "." + vocabulary.Token value + "." + part
