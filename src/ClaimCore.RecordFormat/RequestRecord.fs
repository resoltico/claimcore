namespace ClaimCore.RecordFormat

open System
open ClaimCore.Domain

/// Version-2 canonical request bytes. Stable identity format, also emitted by the wire encoder.
/// This codec supplies bytes only; Application owns hashing and replay admission.
module RequestRecord =
    let private readCommand (element: Text.Json.JsonElement) =
        let path = "$.command"
        let kind = Json.text path "type" element

        let shape fields =
            Json.properties path ("type" :: fields) element

        match kind with
        | "OPEN" ->
            shape [ "registration" ]

            Command.Open(
                Json.registration
                    (path + ".registration")
                    (Json.required path "registration" element)
            )
        | "AMEND_REGISTRATION" ->
            shape [ "registration" ]

            Command.AmendRegistration(
                Json.registration
                    (path + ".registration")
                    (Json.required path "registration" element)
            )
        | "DECIDE" ->
            shape [ "decision" ]

            Command.Decide(
                Json.decision (path + ".decision") (Json.required path "decision" element)
            )
        | "WITHDRAW_DECISION" ->
            shape []
            Command.WithdrawDecision
        | "RECORD_PAYMENT" ->
            shape [ "paymentDate" ]
            Command.RecordPayment(Json.text path "paymentDate" element)
        | "CLEAR_PAYMENT" ->
            shape []
            Command.ClearPayment
        | "CLOSE" ->
            shape []
            Command.Close
        | "REOPEN" ->
            shape []
            Command.Reopen
        | _ ->
            Json.reject
                "$.command.type"
                "Unknown command type. Use capabilities to discover supported commands."

    /// Strict canonical-command-format-2 decoding shared by durable recovery and imports.
    let decode maxBytes (bytes: byte array) =
        if bytes.Length > maxBytes then
            Error
                {
                    Field = "$"
                    Message = $"Request exceeds the {maxBytes:N0}-byte limit."
                }
        else
            Json.parse bytes (fun root ->
                Json.properties
                    "$"
                    [
                        "protocolVersion"
                        "operationId"
                        "caseReference"
                        "expectedVersion"
                        "command"
                    ]
                    root

                if
                    Json.integer "$" "protocolVersion" root
                    <> int64 RecordVersions.CanonicalCommandFormat
                then
                    Json.reject
                        "$.protocolVersion"
                        $"Only canonical command format {RecordVersions.CanonicalCommandFormat} is supported."

                let idText = Json.text "$" "operationId" root

                let operationId =
                    match Guid.TryParseExact(idText, "D") with
                    | true, value when value <> Guid.Empty && value.ToString("D") = idText -> value
                    | _ ->
                        Json.reject
                            "$.operationId"
                            "Use one non-empty UUID in canonical lowercase hyphenated form."

                {
                    OperationId = operationId
                    CaseReference = Json.text "$" "caseReference" root
                    ExpectedVersion = Json.integer "$" "expectedVersion" root
                    Command = readCommand (Json.required "$" "command" root)
                })

    /// Fixed field order makes idempotency insensitive to JSON whitespace/property ordering.
    /// Authored scalar spellings are retained: "1.00" and "1" are deliberately different requests.
    let encode (request: CommandRequest) =
        Json.encode (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("protocolVersion", RecordVersions.CanonicalCommandFormat)
            writer.WriteString("operationId", request.OperationId)
            writer.WriteString("caseReference", request.CaseReference)
            writer.WriteNumber("expectedVersion", request.ExpectedVersion)
            writer.WriteStartObject("command")
            writer.WriteString("type", Commands.name request.Command)

            match request.Command with
            | Command.Open registration
            | Command.AmendRegistration registration -> Json.writeRegistration writer registration
            | Command.Decide decision -> Json.writeDecision writer decision
            | Command.RecordPayment date -> writer.WriteString("paymentDate", date)
            | Command.ClearPayment
            | Command.WithdrawDecision
            | Command.Close
            | Command.Reopen -> ()

            writer.WriteEndObject()
            writer.WriteEndObject())
