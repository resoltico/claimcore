namespace ClaimCore.Web

open System
open System.Text.Json

/// Validate every decoded JSON name and string before endpoint-specific request admission.
module internal JsonUnicode =
    let rec private visit (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Object ->
            for property in element.EnumerateObject() do
                property.Name |> ignore
                visit property.Value
        | JsonValueKind.Array -> element.EnumerateArray() |> Seq.iter visit
        | JsonValueKind.String -> element.GetString() |> ignore
        | _ -> ()

    let validDecodedStrings root =
        try
            visit root
            true
        with :? InvalidOperationException ->
            false
