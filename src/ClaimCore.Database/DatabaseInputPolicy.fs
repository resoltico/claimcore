namespace ClaimCore.Database

module internal DatabaseInputPolicy =
    let private witnessIdentity =
        function
        | DatabaseInputProblem.WitnessSettingMissing ->
            Some("DB_WITNESS_SETTING_MISSING", "A required private witness setting is missing.")
        | DatabaseInputProblem.WitnessFileRefused ->
            Some(
                "DB_WITNESS_FILE_REFUSED",
                "A private witness input file could not be read securely."
            )
        | DatabaseInputProblem.WitnessFileInvalid ->
            Some("DB_WITNESS_FILE_INVALID", "A private witness input is invalid.")
        | DatabaseInputProblem.PrincipalFileRefused ->
            Some(
                "DB_PRINCIPAL_FILE_REFUSED",
                "The private initial-owner principal file could not be read securely."
            )
        | DatabaseInputProblem.PrincipalFileInvalid ->
            Some("DB_PRINCIPAL_FILE_INVALID", "The private initial-owner principal is invalid.")
        | _ -> None

    let private privateEvidence =
        function
        | DatabaseInputProblem.ManagedCopyFileRefused ->
            Some(
                "DB_MANAGED_COPY_FILE_REFUSED",
                "A private managed-copy input is invalid or unreadable."
            )
        | DatabaseInputProblem.ErasureProposalFileRefused ->
            Some(
                "DB_ERASURE_PROPOSAL_FILE_REFUSED",
                "A private erasure proposal is invalid or unreadable."
            )
        | DatabaseInputProblem.SuppressionKeyFileRefused ->
            Some(
                "DB_SUPPRESSION_KEY_FILE_REFUSED",
                "The private suppression key is missing or invalid."
            )
        | DatabaseInputProblem.RestoreEvidenceFileRefused ->
            Some(
                "DB_RESTORE_EVIDENCE_FILE_REFUSED",
                "Private restored-pair evidence is missing or invalid."
            )
        | DatabaseInputProblem.BackupHealthFileRefused ->
            Some(
                "DB_BACKUP_HEALTH_FILE_REFUSED",
                "Private backup-health evidence is missing or invalid."
            )
        | DatabaseInputProblem.WriterHandoffFileRefused ->
            Some(
                "DB_WRITER_HANDOFF_FILE_REFUSED",
                "Private writer-handoff abort evidence is missing or invalid."
            )
        | DatabaseInputProblem.InstallationLossFileRefused ->
            Some(
                "DB_INSTALLATION_LOSS_FILE_REFUSED",
                "Private installation-loss evidence is missing or invalid."
            )
        | DatabaseInputProblem.PhysicalCopyProofFileRefused ->
            Some(
                "DB_PHYSICAL_COPY_PROOF_FILE_REFUSED",
                "Private physical-copy proof is missing or invalid."
            )
        | _ -> None

    let private ordinary =
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
        | _ -> invalidOp "Only ordinary administration input problems belong to this policy."

    let policy reason =
        match witnessIdentity reason |> Option.orElseWith (fun () -> privateEvidence reason) with
        | Some value -> value
        | None -> ordinary reason
