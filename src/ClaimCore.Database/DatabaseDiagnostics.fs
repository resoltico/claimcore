namespace ClaimCore.Database

open System.Buffers
open System.Globalization
open System.Text.Json
open ClaimCore.Postgres

module DatabaseDiagnostics =
    let private group0 =
        [
            AdministrationFailure.OwnerConnectionInvalid,
            ("DB_OWNER_CONNECTION_INVALID",
             "Use an explicit schema-owner connection without unsafe connection options.")
            AdministrationFailure.OwnerIdentityRejected,
            ("DB_OWNER_IDENTITY_REJECTED", "The connected identity is not an admitted schema owner.")
            AdministrationFailure.PostgresVersionUnsupported,
            ("DB_POSTGRES_VERSION_UNSUPPORTED",
             "The PostgreSQL server does not match the required baseline.")
            AdministrationFailure.DatabaseConfigurationInvalid,
            ("DB_DATABASE_CONFIGURATION_INVALID",
             "The database environment does not satisfy the required settings.")
            AdministrationFailure.CatalogUnreadable,
            ("DB_CATALOG_UNREADABLE", "The database catalog could not be qualified.")
        ]

    let private group1 =
        [
            AdministrationFailure.BaselineMissing,
            ("DB_BASELINE_MISSING",
             "No ClaimCore installation exists. Explicitly initialize a fresh baseline with its business time zone.")
            AdministrationFailure.UnsupportedInstallation,
            ("DB_INSTALLATION_UNSUPPORTED",
             "The existing ClaimCore schema is unsupported and was left untouched. Use a separate fresh installation; no upgrade or reset is provided.")
            AdministrationFailure.BaselineIdentityMismatch,
            ("DB_BASELINE_IDENTITY_MISMATCH",
             "The installed baseline identity or digest differs. The installation was left untouched; no repair or conversion is provided.")
            AdministrationFailure.BaselineMarkerWriteFailed,
            ("DB_BASELINE_MARKER_WRITE_FAILED",
             "The atomic baseline identity could not be recorded.")
            AdministrationFailure.BusinessZoneInvalid,
            ("DB_BUSINESS_ZONE_INVALID",
             "Choose a canonical IANA business time zone supported by this runtime.")
        ]

    let private group2 =
        [
            AdministrationFailure.BusinessZoneAlreadyConfigured,
            ("DB_BUSINESS_ZONE_ALREADY_CONFIGURED",
             "The installation already has a different immutable business time zone.")
            AdministrationFailure.InstallationLineageMissing,
            ("DB_INSTALLATION_LINEAGE_MISSING", "The installation lineage is missing.")
            AdministrationFailure.PruneOptionsInvalid,
            ("DB_PRUNE_OPTIONS_INVALID",
             "Retention periods and batch size must remain within the supported bounds.")
            AdministrationFailure.PruneAuditFailed,
            ("DB_PRUNE_AUDIT_FAILED", "The preparation-maintenance audit could not be recorded.")
        ]

    let private group3 =
        [
            AdministrationFailure.RecoveryFootprintUnreadable,
            ("DB_RECOVERY_FOOTPRINT_UNREADABLE",
             "The terminal recovery footprint could not be read.")
            AdministrationFailure.SchemaDefinitionInvalid,
            ("DB_SCHEMA_DEFINITION_INVALID",
             "The current schema definition or required installed structure is invalid or unavailable. No repair was attempted.")
            AdministrationFailure.DatabaseUnavailable,
            ("DB_DATABASE_UNAVAILABLE", "Database access failed before commit was attempted.")
            AdministrationFailure.OperationFailed,
            ("DB_OPERATION_FAILED", "Maintenance did not produce a confirmed result.")
            AdministrationFailure.CommitUnconfirmed,
            ("DB_COMMIT_UNCONFIRMED",
             "Commit was attempted but not confirmed. Inspect and reconcile the database before any repeat.")
        ]

    let private native = group0 @ group1 @ group2 @ group3

    let private inputPolicy =
        function
        | DatabaseInputProblem.UnsupportedInvocation ->
            "DB_INVOCATION_UNSUPPORTED", "Run ClaimCore.Database help for supported arguments."
        | DatabaseInputProblem.UnknownOption -> "DB_OPTION_UNKNOWN", "An option is not supported."
        | DatabaseInputProblem.MissingOptionValue _ ->
            "DB_OPTION_VALUE_MISSING", "A known option requires a value."
        | DatabaseInputProblem.RepeatedOption _ ->
            "DB_OPTION_REPEATED", "A known option was supplied more than once."
        | DatabaseInputProblem.OptionOutOfRange _ ->
            "DB_OPTION_OUT_OF_RANGE", "An option value is outside its supported range."
        | DatabaseInputProblem.ConnectionSettingMissing ->
            "DB_CONNECTION_SETTING_MISSING",
            "Set CLAIMCORE_ADMIN_CONNECTION_FILE to the private schema-owner connection file."
        | DatabaseInputProblem.ConnectionFileRefused ->
            "DB_CONNECTION_FILE_REFUSED",
            "The schema-owner connection file could not be read securely."
        | DatabaseInputProblem.ConnectionFileEmpty ->
            "DB_CONNECTION_FILE_EMPTY", "The schema-owner connection file is empty."
        | DatabaseInputProblem.ProcessFailed ->
            "DB_PROCESS_FAILED",
            "The administration process failed. Inspect state before continuing."
        | DatabaseInputProblem.OutputDeliveryFailed ->
            "DB_OUTPUT_DELIVERY_FAILED",
            "The administration result could not be delivered completely. The operation outcome below remains authoritative for this process."

    let private encode write =
        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer)
        write writer
        writer.Flush()
        Array.append (buffer.WrittenSpan.ToArray()) [| byte '\n' |]

    let private diagnostic (writer: Utf8JsonWriter) (id: string) parameters =
        writer.WritePropertyName("diagnostic")
        writer.WriteStartObject()
        writer.WriteString("id", id)
        writer.WritePropertyName("parameters")
        writer.WriteStartObject()
        parameters writer
        writer.WriteEndObject()
        writer.WriteEndObject()

    let private inputParameters reason (writer: Utf8JsonWriter) =
        match reason with
        | DatabaseInputProblem.MissingOptionValue option
        | DatabaseInputProblem.RepeatedOption option ->
            writer.WriteString("option", DatabaseOptions.token option)
        | DatabaseInputProblem.OptionOutOfRange option ->
            writer.WriteString("option", DatabaseOptions.token option)
            writer.WriteNumber("minimum", 1)
            writer.WriteNumber("maximum", DatabaseOptions.maximum option)
        | _ -> ()

    let inputFailure reason =
        let id, message = inputPolicy reason

        encode (fun writer ->
            writer.WriteStartObject()

            writer.WriteString(
                "kind",
                if reason = DatabaseInputProblem.ProcessFailed then
                    "administrationProcessFailure"
                else
                    "administrationInputFailure"
            )

            diagnostic writer id (inputParameters reason)
            writer.WriteString("message", message)

            writer.WriteString(
                "operationOutcome",
                if reason = DatabaseInputProblem.ProcessFailed then
                    "COMPLETION_UNKNOWN"
                else
                    "NOT_STARTED"
            )

            writer.WriteString(
                "recommendedAction",
                if reason = DatabaseInputProblem.ProcessFailed then
                    "INSPECT_AND_RECONCILE"
                else
                    "CORRECT_CONFIGURATION_OR_INVOCATION"
            )

            writer.WriteEndObject())

    let outcomeToken =
        function
        | AdministrationOutcome.Completed _ -> "COMPLETED"
        | AdministrationOutcome.CompletedCleanupFailed _ -> "COMPLETED_CLEANUP_FAILED"
        | AdministrationOutcome.NotStarted _ -> "NOT_STARTED"
        | AdministrationOutcome.NotCommitted _ -> "NOT_COMMITTED"
        | AdministrationOutcome.CompletionUnknown _ -> "COMPLETION_UNKNOWN"

    let exitCode =
        function
        | AdministrationOutcome.Completed _ -> 0
        | AdministrationOutcome.CompletionUnknown _ -> 4
        | _ -> 3

    let private writeCounts (writer: Utf8JsonWriter) (result: PreparationPruneResult option) =
        writer.WritePropertyName("maintenance")

        match result with
        | None -> writer.WriteNullValue()
        | Some value ->
            writer.WriteStartObject()
            writer.WriteNumber("candidateCount", value.CandidateCount)
            writer.WriteNumber("deletedCount", value.DeletedCount)
            writer.WriteBoolean("dryRun", value.DryRun)

            writer.WriteString(
                "terminalPreparationCount",
                value.TerminalPreparationCount.ToString(CultureInfo.InvariantCulture)
            )

            writer.WriteString(
                "terminalCanonicalRequestBytes",
                value.TerminalCanonicalRequestBytes.ToString(CultureInfo.InvariantCulture)
            )

            writer.WriteEndObject()

    let private outcomeDetail writer outcome =
        match outcome with
        | AdministrationOutcome.Completed value -> writeCounts writer value
        | AdministrationOutcome.CompletedCleanupFailed value ->
            diagnostic writer "DB_COMPLETED_CLEANUP_FAILED" ignore

            writer.WriteString(
                "message",
                "The operation completed, but local cleanup failed. Do not repeat the operation solely because of this failure."
            )

            writeCounts writer value
        | AdministrationOutcome.NotStarted reason
        | AdministrationOutcome.NotCommitted reason
        | AdministrationOutcome.CompletionUnknown reason ->
            let id, message = native |> List.find (fst >> (=) reason) |> snd
            diagnostic writer id ignore
            writer.WriteString("message", message)

    let outcome command result =
        encode (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("kind", "administrationResult")
            writer.WriteString("command", DatabaseArguments.commandToken command)
            writer.WriteString("operationOutcome", outcomeToken result)
            outcomeDetail writer result

            writer.WriteString(
                "recommendedAction",
                if exitCode result = 0 then
                    "NONE"
                else
                    "INSPECT_AND_RECONCILE"
            )

            writer.WriteEndObject())

    let deliveryFailure command result =
        encode (fun writer ->
            let id, message = inputPolicy DatabaseInputProblem.OutputDeliveryFailed
            writer.WriteStartObject()
            writer.WriteString("kind", "administrationDeliveryFailure")
            writer.WriteString("command", DatabaseArguments.commandToken command)
            writer.WriteString("operationOutcome", outcomeToken result)
            diagnostic writer id ignore
            writer.WriteString("message", message)
            writer.WriteString("recommendedAction", "INSPECT_AND_RECONCILE")
            writer.WriteEndObject())

    let nativeReasons = native |> List.map fst

    let nativeToken reason =
        native |> List.find (fst >> (=) reason) |> snd |> fst

    let inputToken reason = inputPolicy reason |> fst

    let inputReasons =
        [
            DatabaseInputProblem.UnsupportedInvocation
            DatabaseInputProblem.UnknownOption
            DatabaseInputProblem.ConnectionSettingMissing
            DatabaseInputProblem.ConnectionFileRefused
            DatabaseInputProblem.ConnectionFileEmpty
        ]
        @ (DatabaseOptions.all |> List.map DatabaseInputProblem.RepeatedOption)
        @ (DatabaseOptions.all
           |> List.filter ((<>) DatabaseOption.DryRun)
           |> List.collect (fun option ->
               [
                   DatabaseInputProblem.MissingOptionValue option
                   DatabaseInputProblem.OptionOutOfRange option
               ]))
