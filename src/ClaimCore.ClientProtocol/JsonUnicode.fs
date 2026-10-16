namespace ClaimCore.ClientProtocol

open System
open System.Text.Json

module internal JsonUnicode =
    let private isPersisted (value: string) =
        try
            System.Text.UTF8Encoding(false, true).GetByteCount(value) |> ignore

            not (value.Contains '\u0000')
            && (value.EnumerateRunes()
                |> Seq.forall (fun rune ->
                    not (rune.Value >= 0xFDD0 && rune.Value <= 0xFDEF)
                    && (rune.Value &&& 0xFFFE) <> 0xFFFE))
        with :? System.Text.EncoderFallbackException ->
            false

    let rec private visit (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Object ->
            element.EnumerateObject()
            |> Seq.forall (fun item -> isPersisted item.Name && visit item.Value)
        | JsonValueKind.Array -> element.EnumerateArray() |> Seq.forall visit
        | JsonValueKind.String -> element.GetString() |> Option.ofObj |> Option.exists isPersisted
        | _ -> true

    let validDecodedStrings root =
        try
            visit root
        with :? InvalidOperationException ->
            false
