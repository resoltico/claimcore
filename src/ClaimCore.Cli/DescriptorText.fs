namespace ClaimCore.Cli

open System
open System.Text.Json

/// A mechanical projection of generated metadata; no command names or admission rules live here.
module DescriptorText =
    let private text (value: JsonElement) = value.GetString() |> nonNull

    let private localized language (value: JsonElement) (name: string) =
        let fallback = value.GetProperty(name).GetString() |> nonNull

        match value.TryGetProperty(name + "Id") with
        | true, identity -> ContractDiscovery.localized language (text identity) fallback
        | _ -> fallback

    let private fieldLabel language (definitions: JsonElement) (input: JsonElement) =
        let name = input.GetProperty("fieldName") |> text

        let definition =
            match input.TryGetProperty "labelId" with
            | true, _ -> Some input
            | _ ->
                [ "fields"; "inputFields"; "authorityFields" ]
                |> Seq.collect (fun key -> definitions.GetProperty(key).EnumerateArray())
                |> Seq.tryFind (fun item -> item.GetProperty("name") |> text = name)

        match definition with
        | Some value -> name + " — " + localized language value "label"
        | None -> name

    let rec private renderValue language definitions depth (value: JsonElement) =
        match value.ValueKind with
        | JsonValueKind.Object -> renderObject language definitions depth value
        | JsonValueKind.Array ->
            let rows =
                value.EnumerateArray()
                |> Seq.map (renderValue language definitions depth)
                |> Seq.toList

            if rows.IsEmpty then "[]" else String.concat "\n" rows
        | JsonValueKind.String -> text value
        | _ -> value.GetRawText()

    and private renderObject language definitions depth (value: JsonElement) =
        let indent = String(' ', depth * 2)
        let hasField = value.TryGetProperty "fieldName" |> fst

        let rows =
            value.EnumerateObject()
            |> Seq.filter (fun item ->
                not (List.contains item.Name [ "labelId"; "meaningId"; "confirmationId" ])
                && not (hasField && item.Name = "label"))
            |> Seq.map (fun item ->
                let content =
                    match item.Name with
                    | "fieldName" -> fieldLabel language definitions value
                    | "label"
                    | "meaning" -> localized language value item.Name
                    | _ -> renderValue language definitions (depth + 1) item.Value

                let separator =
                    if
                        item.Value.ValueKind = JsonValueKind.Object
                        || item.Value.ValueKind = JsonValueKind.Array
                    then
                        "\n"
                    else
                        " "

                indent + item.Name + ":" + separator + content)
            |> Seq.toList

        if rows.IsEmpty then "{}" else String.concat "\n" rows

    let render language definitions value =
        renderValue language definitions 0 value
