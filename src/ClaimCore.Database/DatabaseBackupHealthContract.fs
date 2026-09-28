namespace ClaimCore.Database

open System.Text.Json.Nodes

/// The owner readback reports file observation, never a current health admission.
module internal DatabaseBackupHealthContract =
    let private text (value: string) : JsonNode =
        JsonValue.Create(value)
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Readback schema token is absent.")
        |> fun node -> node :> JsonNode

    let private obj properties : JsonNode =
        let value = JsonObject()
        properties |> List.iter (fun (name, item) -> value.Add(name, item))
        value

    let private values items =
        let value = JsonArray()
        items |> List.iter value.Add
        value :> JsonNode

    let private constant value = obj [ "const", text value ]

    let private enum items =
        obj [ "enum", items |> List.map text |> values ]

    let schema () =
        let properties =
            [
                "format", constant "claimcore-backup-health-publication-readback-1"
                "command", constant "RECONCILE_BACKUP_HEALTH"
                "publicationState",
                enum
                    [ "COMPLETE"; "PARTIAL_CERTIFICATE"; "PARTIAL_SIGNATURE"; "MISSING"; "UNKNOWN" ]
                "expectedCertificateSha256",
                obj
                    [
                        "type", values [ text "string"; text "null" ]
                        "pattern", text "^[0-9a-f]{64}$"
                    ]
                "realDataReady", obj [ "const", JsonValue.Create(false) ]
                "recommendedAction",
                enum
                    [
                        "REVERIFY_CURRENT_HEALTH"
                        "PRESERVE_PARTIAL_AND_ISSUE_FRESH_AT_NEW_PATH"
                        "QUARANTINE_AND_INSPECT"
                        "ISSUE_FRESH_AT_NEW_PATH"
                    ]
            ]

        obj
            [
                "type", text "object"
                "additionalProperties", JsonValue.Create(false)
                "properties", obj properties
                "required", properties |> List.map (fst >> text) |> values
            ]
