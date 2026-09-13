namespace ClaimCore.Docs

open System
open System.IO
open System.Text.Json

[<RequireQualifiedAccess>]
module ConvergenceJson =
    let private ordinal =
        List.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))

    let exact (expected: string list) (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            Error "Expected a JSON object."
        else
            let names = element.EnumerateObject() |> Seq.map _.Name |> Seq.toList

            if names.Length <> (names |> Set.ofList |> Set.count) then
                Error "Duplicate JSON properties are forbidden."
            elif Set.ofList names <> Set.ofList expected then
                Error "JSON object has missing or unknown properties."
            else
                Ok()

    let text (name: string) (element: JsonElement) =
        let value = element.GetProperty(name)

        match value.ValueKind, value.GetString() |> Option.ofObj with
        | JsonValueKind.String, Some content when not (String.IsNullOrWhiteSpace(content)) ->
            Ok content
        | _ -> Error $"JSON property '{name}' must be nonblank text."

    let integer (name: string) (element: JsonElement) =
        let value = element.GetProperty(name)
        let mutable parsed = 0

        if
            value.ValueKind = JsonValueKind.Number
            && value.TryGetInt32(&parsed)
            && parsed >= 0
        then
            Ok parsed
        else
            Error $"JSON property '{name}' must be a nonnegative integer."

    let strings (name: string) (element: JsonElement) =
        let value = element.GetProperty(name)

        if value.ValueKind <> JsonValueKind.Array then
            Error $"JSON property '{name}' must be an array."
        else
            let parsed =
                value.EnumerateArray()
                |> Seq.map (fun item ->
                    match item.ValueKind, item.GetString() |> Option.ofObj with
                    | JsonValueKind.String, Some text when not (String.IsNullOrWhiteSpace(text)) ->
                        Ok text
                    | _ -> Error $"JSON property '{name}' must contain nonblank text.")
                |> Seq.toList

            let failures =
                parsed
                |> List.choose (function
                    | Error message -> Some message
                    | Ok _ -> None)

            match failures with
            | first :: _ -> Error first
            | [] ->
                let values = parsed |> List.choose Result.toOption

                if
                    values.IsEmpty
                    || values <> ordinal values
                    || values.Length <> (values |> Set.ofList |> Set.count)
                then
                    Error $"JSON property '{name}' must be nonempty, unique, and ordinally sorted."
                else
                    Ok values

    let array (name: string) (element: JsonElement) =
        let value = element.GetProperty(name)

        if value.ValueKind = JsonValueKind.Array then
            Ok(value.EnumerateArray() |> Seq.toList)
        else
            Error $"JSON property '{name}' must be an array."

    let document (root: RepositoryRoot) (relative: string) assess =
        Repository.registeredPath root relative
        |> Result.bind (fun candidate ->
            Repository.ensureExistingSafe root candidate
            |> Result.bind (fun path ->
                try
                    use document = JsonDocument.Parse(File.ReadAllBytes(path))
                    assess path document.RootElement
                with error ->
                    Error(error.GetType().Name + ": " + error.Message)))
