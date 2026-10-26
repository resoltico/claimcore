namespace ClaimCore.Domain

open Microsoft.FSharp.Reflection

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

    let define<'T when 'T: equality> name (all: 'T list) (token: 'T -> string) : Vocabulary<'T> =
        if List.distinct all <> all then
            invalidArg name "Duplicate case."

        let tokens = List.map token all

        if List.distinct tokens <> tokens then
            invalidArg name "Duplicate token."

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
