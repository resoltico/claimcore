module ClaimCore.Tests.AdviceWireFixtures

open System.Text.Json.Nodes
open ClaimCore.Domain
open ClaimCore.Domain.Tasks

/// Complete unrelated availability members so focused raw vectors isolate their intended defect.
let completeAdviceWire (raw: string) =
    let value = JsonNode.Parse raw |> nonNull

    let offered =
        (value["acts"] |> nonNull).AsArray()
        |> Seq.map (fun item -> (item |> nonNull).GetValue<string>())
        |> Set.ofSeq

    let maintenance = value["maintenance"] |> nonNull
    let paused = (maintenance["active"] |> nonNull).GetValue<bool>()
    let blocked = JsonArray()

    for token in
        (Registry.clientDeclarations
         |> List.filter (fun declaration -> declaration.Target = ActSubject.Case)
         |> List.map (fun declaration -> TaskId.token declaration.Id)) do
        if not (Set.contains token offered) then
            let item = JsonObject()
            item["act"] <- JsonValue.Create token
            let reason = JsonObject()

            reason["code"] <-
                JsonValue.Create(
                    if paused then
                        "REGISTER_MAINTENANCE"
                    else
                        "RESOURCE_UNAVAILABLE"
                )

            reason["action"] <-
                JsonValue.Create(if paused then "READ_CURRENT" else "ASK_FOR_ACCESS")

            item["reason"] <- reason
            blocked.Add item

    value["blocked"] <- blocked
    value.ToJsonString()
