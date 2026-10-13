namespace ClaimCore.RecordFormat

open ClaimCore.Domain

/// Canonical stewardship payload within ledger entry format 1.
module StewardshipRecord =
    let private action root =
        let path = "$.action"

        match Json.text path "type" root with
        | "VOID" ->
            Json.properties path [ "type"; "reason" ] root
            StewardshipAction.Void(Json.text path "reason" root)
        | "REINSTATE" ->
            Json.properties path [ "type"; "reason" ] root
            StewardshipAction.Reinstate(Json.text path "reason" root)
        | "ERASE" ->
            Json.properties path [ "type" ] root
            StewardshipAction.Erase
        | _ -> Json.reject (path + ".type") "Unknown stewardship action."

    let decode maxBytes bytes =
        Json.bounded maxBytes bytes (fun root ->
            Json.properties
                "$"
                [ "operationId"; "caseReference"; "expectedVersion"; "action" ]
                root

            {
                OperationId = Json.guid "$" "operationId" root
                CaseReference = Json.text "$" "caseReference" root
                ExpectedVersion = Json.integer "$" "expectedVersion" root
                Action = Json.required "$" "action" root |> action
            })

    let encode (request: StewardshipRequest) =
        Json.encode (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("operationId", request.OperationId)
            writer.WriteString("caseReference", request.CaseReference)
            writer.WriteNumber("expectedVersion", request.ExpectedVersion)
            writer.WriteStartObject("action")

            match request.Action with
            | StewardshipAction.Void reason ->
                writer.WriteString("type", "VOID")
                writer.WriteString("reason", reason)
            | StewardshipAction.Reinstate reason ->
                writer.WriteString("type", "REINSTATE")
                writer.WriteString("reason", reason)
            | StewardshipAction.Erase -> writer.WriteString("type", "ERASE")

            writer.WriteEndObject()
            writer.WriteEndObject())
