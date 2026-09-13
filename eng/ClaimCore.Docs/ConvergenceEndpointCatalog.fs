namespace ClaimCore.Docs

open System
open System.Text.Json

[<RequireQualifiedAccess>]
module ConvergenceEndpointCatalog =
    let private ids (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            Error "Generated endpoint catalog must be a JSON object."
        else
            match element.TryGetProperty("endpoints") with
            | false, _ -> Error "Generated endpoint catalog has no endpoints."
            | true, endpoints when endpoints.ValueKind <> JsonValueKind.Array ->
                Error "Generated endpoints must be an array."
            | true, endpoints ->
                let values =
                    endpoints.EnumerateArray()
                    |> Seq.map (fun item ->
                        if item.ValueKind <> JsonValueKind.Object then
                            None
                        else
                            match item.TryGetProperty("id") with
                            | true, identifier when identifier.ValueKind = JsonValueKind.String ->
                                identifier.GetString() |> Option.ofObj
                            | _ -> None)
                    |> Seq.toList

                let parsed = values |> List.choose id

                if
                    parsed.IsEmpty
                    || parsed.Length <> values.Length
                    || parsed.Length <> (parsed |> Set.ofList |> Set.count)
                    || parsed
                       |> List.exists (fun value ->
                           String.IsNullOrWhiteSpace(value) || value |> Seq.exists Char.IsControl)
                then
                    Error "Generated endpoint identifiers must be nonempty and unique."
                else
                    Ok(Set.ofList parsed)

    let read root relative expectedKind =
        ConvergenceJson.document root relative (fun _ element ->
            if element.ValueKind <> JsonValueKind.Object then
                Error "Generated endpoint catalog must be an object."
            else
                match element.TryGetProperty("contractKind") with
                | true, kind when
                    kind.ValueKind = JsonValueKind.String && kind.GetString() = expectedKind
                    ->
                    ids element
                | _ -> Error "Generated endpoint catalog kind is incompatible.")
