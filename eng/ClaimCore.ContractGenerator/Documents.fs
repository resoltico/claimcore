namespace ClaimCore.ContractGeneration

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open ClaimCore.Contract
open ClaimCore.Contract.Protocol

module ContractDocuments =
    let private json (node: JsonNode) =
        node.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + "\n"
        |> System.Text.Encoding.UTF8.GetBytes

    let descriptors () =
        let root = JsonNode.Parse(ContractDescriptors.render ()) |> nonNull

        root["productVersion"] <- JsonValue.Create(ClaimCore.Contract.BuildIdentity.current.Version)

        json root
