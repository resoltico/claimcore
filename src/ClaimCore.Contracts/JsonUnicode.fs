namespace ClaimCore.Contracts

open System
open System.Text.Json
open ClaimCore.Domain

module internal JsonUnicode =
    let rec private visit (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Object ->
            element.EnumerateObject()
            |> Seq.forall (fun item -> UnicodeText.isPersisted item.Name && visit item.Value)
        | JsonValueKind.Array -> element.EnumerateArray() |> Seq.forall visit
        | JsonValueKind.String ->
            element.GetString() |> Option.ofObj |> Option.exists UnicodeText.isPersisted
        | _ -> true

    let validDecodedStrings root =
        try
            visit root
        with :? InvalidOperationException ->
            false
