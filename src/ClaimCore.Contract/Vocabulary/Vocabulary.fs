namespace ClaimCore.Contract

open Microsoft.FSharp.Reflection

[<NoEquality; NoComparison>]
type Vocabulary<'T when 'T: equality> =
    private
        {
            Name: string
            Definitions: ('T * string) list
        }

module Vocabulary =
    let private representation =
        System.Reflection.BindingFlags.Public
        ||| System.Reflection.BindingFlags.NonPublic

    let define<'T when 'T: equality> name (definitions: ('T * string) list) : Vocabulary<'T> =
        let cases = List.map fst definitions
        let tokens = List.map snd definitions

        if List.distinct cases <> cases then
            invalidArg name "Duplicate case."

        if List.distinct tokens <> tokens then
            invalidArg name "Duplicate token."

        if FSharpType.IsUnion(typeof<'T>, representation) then
            let expected =
                FSharpType.GetUnionCases(typeof<'T>, representation)
                |> Array.map _.Tag
                |> Set.ofArray

            let actual =
                cases
                |> List.map (fun value ->
                    let case, _ = FSharpValue.GetUnionFields(value, typeof<'T>, representation)
                    case.Tag)
                |> Set.ofList

            if actual <> expected then
                invalidArg name "Every union variant needs a token."

        {
            Name = name
            Definitions = definitions
        }

    let entries vocabulary = vocabulary.Definitions
    let all vocabulary = vocabulary.Definitions |> List.map fst
    let tokens vocabulary = vocabulary.Definitions |> List.map snd

    let token vocabulary value =
        vocabulary.Definitions |> List.find (fst >> (=) value) |> snd

    let tryParse vocabulary text =
        vocabulary.Definitions |> List.tryFind (snd >> (=) text) |> Option.map fst

    let messageKey vocabulary value part =
        vocabulary.Name + "." + token vocabulary value + "." + part
