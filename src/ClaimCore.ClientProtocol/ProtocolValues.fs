namespace ClaimCore.ClientProtocol

open System.Text.Json.Nodes

module internal ProtocolSchema =
    let text = Schema.string None None (Some 1) None
    let token value = Schema.constant (TextConstant value)
    let property name schema = Schema.property name schema true
    let objectOf properties = Schema.objectOf false properties

    let document name root definitions =
        {
            Identifier = "https://claimcore.local/contracts/" + name
            Title = "ClaimCore " + name
            Root = root
            Definitions = definitions
        }

module internal ProtocolJson =
    let text (value: string) : JsonNode =
        (JsonValue.Create value |> nonNull) :> JsonNode

    let number (value: int) : JsonNode = (JsonValue.Create value) :> JsonNode

    let obj (properties: (string * (JsonNode | null)) list) : JsonNode =
        let result = JsonObject()
        properties |> List.iter (fun (name, value) -> result.Add(name, value))
        result

    let optional (write: 'a -> JsonNode) value : JsonNode | null =
        match value with
        | Some value -> write value
        | None -> null
