namespace ClaimCore.Docs

open System
open System.Text.Json

[<RequireQualifiedAccess>]
module ConvergenceOutcomeCatalog =
    let private token (name: string) (branch: JsonElement) =
        let value = branch.GetProperty("properties").GetProperty(name).GetProperty("const")

        if value.ValueKind <> JsonValueKind.String then
            invalidOp "Generated outcome discriminator must be text."

        value.GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Generated outcome discriminator is null.")

    let private cliTags (schema: JsonElement) =
        schema.GetProperty("$defs").GetProperty("root").GetProperty("oneOf").EnumerateArray()
        |> Seq.collect (fun branch ->
            match branch.TryGetProperty("oneOf") with
            | true, nested -> nested.EnumerateArray() |> Seq.toList
            | false, _ -> [ branch ])
        |> Seq.map (token "kind")
        |> Seq.toList

    let private webTags (schema: JsonElement) =
        schema
            .GetProperty("$defs")
            .GetProperty("root")
            .GetProperty("properties")
            .GetProperty("outcome")
            .GetProperty("oneOf")
            .EnumerateArray()
        |> Seq.map (token "tag")
        |> Seq.toList

    let private endpointTags variant (endpoint: JsonElement) =
        let identifier = endpoint.GetProperty("id").GetString()

        let id =
            identifier
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp "Generated endpoint ID is null.")

        let schema = endpoint.GetProperty("responseSchema")

        let tags =
            match variant with
            | "cli" -> cliTags schema
            | "web" -> webTags schema
            | _ -> invalidOp "Unknown generated outcome catalog variant."

        if tags.IsEmpty then
            invalidOp "Generated endpoint outcome tags must be nonempty."

        id, tags |> Set.ofList |> Set.toList

    let read root relative expectedKind variant =
        ConvergenceJson.document root relative (fun _ element ->
            let kind = element.GetProperty("contractKind").GetString()

            if kind <> expectedKind then
                Error "Generated outcome catalog kind is incompatible."
            else
                let entries =
                    element.GetProperty("endpoints").EnumerateArray()
                    |> Seq.map (endpointTags variant)
                    |> Seq.toList

                let ids = entries |> List.map fst

                if ids.IsEmpty || ids.Length <> (ids |> Set.ofList |> Set.count) then
                    Error "Generated outcome catalog endpoint IDs must be unique."
                else
                    Ok(Map.ofList entries))
