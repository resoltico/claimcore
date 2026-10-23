namespace ClaimCore.ContractGeneration

open System.Text.Json
open ClaimCore.Contract

module DefaultPresentation =
    let render () =
        let values =
            AuthoredMessages.templates "en"
            |> Map.filter (fun key _ ->
                not (key.StartsWith("cli.", System.StringComparison.Ordinal)))
            |> Map.map (fun _ template ->
                Template.parts template
                |> List.map (function
                    | TemplatePart.Literal value -> Map.ofList [ "literal", value ]
                    | TemplatePart.Hole name -> Map.ofList [ "hole", name ]))

        ContractJson.contract (fun writer -> JsonSerializer.Serialize(writer, values))
