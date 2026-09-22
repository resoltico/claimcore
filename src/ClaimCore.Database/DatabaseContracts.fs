namespace ClaimCore.Database

open System
open System.Text
open System.Text.Json.Nodes
open System.Security.Cryptography
open ClaimCore.Postgres

/// Owner-only administration has its own exact response grammar, with no case-work dependency.
/// JSON nodes construct schema documents only; diagnostic arguments remain closed native types.
module DatabaseContracts =
    let private obj (properties: (string * JsonNode) list) : JsonNode =
        let value = JsonObject()
        properties |> List.iter (fun (key, item) -> value.Add(key, item))
        value

    let private text (value: string) : JsonNode =
        JsonValue.Create(value)
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Schema tokens must be non-null.")
        |> fun node -> node :> JsonNode

    let private arr (values: JsonNode list) : JsonNode =
        let result = JsonArray()
        values |> List.iter (fun value -> result.Add value)
        result

    let private constant value = obj [ "const", text value ]
    let private kind value = obj [ "type", text value ]

    let private integer minimum maximum =
        obj
            [
                "type", text "integer"
                "minimum", JsonValue.Create(minimum: int)
                "maximum", JsonValue.Create(maximum: int)
            ]

    let private enumeration values =
        obj [ "enum", values |> List.map text |> arr ]

    let private exact properties =
        obj
            [
                "type", text "object"
                "additionalProperties", JsonValue.Create(false)
                "properties", obj properties
                "required", properties |> List.map (fst >> text) |> arr
            ]

    let private message () =
        obj [ "type", text "string"; "minLength", JsonValue.Create(1) ]

    let private diagnostic id parameters =
        exact [ "id", constant id; "parameters", exact parameters ]

    let private action value = "recommendedAction", constant value
    let private commands = [ "MIGRATE"; "SET_BUSINESS_ZONE"; "PRUNE" ]

    let private inputParameters =
        function
        | DatabaseInputProblem.MissingOptionValue option
        | DatabaseInputProblem.RepeatedOption option ->
            [ "option", constant (DatabaseOptions.token option) ]
        | DatabaseInputProblem.OptionOutOfRange option ->
            [
                "option", constant (DatabaseOptions.token option)
                "minimum", obj [ "const", JsonValue.Create(1) ]
                "maximum", obj [ "const", JsonValue.Create(DatabaseOptions.maximum option) ]
            ]
        | _ -> []

    let private input reason =
        exact
            [
                "kind", constant "administrationInputFailure"
                "diagnostic",
                diagnostic (DatabaseDiagnostics.inputToken reason) (inputParameters reason)
                "message", message ()
                "operationOutcome", constant "NOT_STARTED"
                action "CORRECT_CONFIGURATION_OR_INVOCATION"
            ]

    let private count () =
        obj
            [
                "type", text "string"
                "pattern",
                text
                    "^(?:0|[1-9][0-9]{0,17}|[1-8][0-9]{18}|9[0-1][0-9]{17}|92[0-1][0-9]{16}|922[0-2][0-9]{15}|9223[0-2][0-9]{14}|92233[0-6][0-9]{13}|922337[0-1][0-9]{12}|92233720[0-2][0-9]{10}|922337203[0-5][0-9]{9}|9223372036[0-7][0-9]{8}|92233720368[0-4][0-9]{7}|922337203685[0-3][0-9]{6}|9223372036854[0-6][0-9]{5}|92233720368547[0-6][0-9]{4}|922337203685477[0-4][0-9]{3}|9223372036854775[0-7][0-9]{2}|922337203685477580[0-6]|9223372036854775807)$"
            ]

    let private maintenance command =
        if command <> "PRUNE" then
            kind "null"
        else
            exact
                [
                    "candidateCount", integer 0 1000
                    "deletedCount", integer 0 1000
                    "dryRun", kind "boolean"
                    "terminalPreparationCount", count ()
                    "terminalCanonicalRequestBytes", count ()
                ]

    let private completed command cleanup =
        let details =
            if cleanup then
                [
                    "diagnostic", diagnostic "DB_COMPLETED_CLEANUP_FAILED" []
                    "message", message ()
                ]
            else
                []

        exact (
            [
                "kind", constant "administrationResult"
                "command", constant command
                "operationOutcome",
                constant (if cleanup then "COMPLETED_CLEANUP_FAILED" else "COMPLETED")
                "maintenance", maintenance command
                action (if cleanup then "INSPECT_AND_RECONCILE" else "NONE")
            ]
            @ details
        )

    let private failed phase reasons =
        exact
            [
                "kind", constant "administrationResult"
                "command", enumeration commands
                "operationOutcome", constant phase
                "diagnostic",
                exact
                    [
                        "id", reasons |> List.map DatabaseDiagnostics.nativeToken |> enumeration
                        "parameters", exact []
                    ]
                "message", message ()
                action "INSPECT_AND_RECONCILE"
            ]

    let private processFailure () =
        exact
            [
                "kind", constant "administrationProcessFailure"
                "diagnostic", diagnostic "DB_PROCESS_FAILED" []
                "message", message ()
                "operationOutcome", constant "COMPLETION_UNKNOWN"
                action "INSPECT_AND_RECONCILE"
            ]

    let private delivery () =
        exact
            [
                "kind", constant "administrationDeliveryFailure"
                "command", enumeration commands
                "operationOutcome",
                enumeration
                    [
                        "COMPLETED"
                        "COMPLETED_CLEANUP_FAILED"
                        "NOT_STARTED"
                        "NOT_COMMITTED"
                        "COMPLETION_UNKNOWN"
                    ]
                "diagnostic", diagnostic "DB_OUTPUT_DELIVERY_FAILED" []
                "message", message ()
                action "INSPECT_AND_RECONCILE"
            ]

    let private responseSchema () =
        let definite =
            DatabaseDiagnostics.nativeReasons
            |> List.filter ((<>) AdministrationFailure.CommitUnconfirmed)

        let variants =
            (DatabaseDiagnostics.inputReasons |> List.map input)
            @ (commands
               |> List.collect (fun command -> [ completed command false; completed command true ]))
            @ [
                failed "NOT_STARTED" definite
                failed "NOT_COMMITTED" definite
                failed "COMPLETION_UNKNOWN" [ AdministrationFailure.CommitUnconfirmed ]
                processFailure ()
                delivery ()
            ]

        obj
            [
                "$schema", text "https://json-schema.org/draft/2020-12/schema"
                "$id",
                text "https://claimcore.local/contracts/administration-v1.response.schema.json"
                "oneOf", arr variants
            ]

    let schema () =
        responseSchema().ToJsonString() + "\n" |> Encoding.UTF8.GetBytes

    let catalogue () =
        let schemaValue = responseSchema ()

        let fingerprint =
            schemaValue.ToJsonString()
            |> Encoding.UTF8.GetBytes
            |> SHA256.HashData
            |> Convert.ToHexStringLower

        obj
            [
                "contractKind", text "ADMINISTRATION_DIAGNOSTICS_V1"
                "fingerprint", text fingerprint
                "responseSchema", schemaValue
            ]
        |> _.ToJsonString()
        |> fun value -> Encoding.UTF8.GetBytes(value + "\n")
