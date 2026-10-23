namespace ClaimCore.ContractGeneration

open System.Text.Json
open ClaimCore.Contract

module DefaultPresentation =
    let render () =
        let values =
            MessageText.all
            |> Map.filter (fun key _ ->
                not (key.StartsWith("cli.", System.StringComparison.Ordinal)))
            |> Map.map (fun _ parts ->
                parts
                |> List.map (function
                    | MessagePart.Literal value -> Map.ofList [ "literal", value ]
                    | MessagePart.Hole name -> Map.ofList [ "hole", name ]))

        ContractJson.contract (fun writer -> JsonSerializer.Serialize(writer, values))
