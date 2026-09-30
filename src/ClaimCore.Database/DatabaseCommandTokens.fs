namespace ClaimCore.Database

/// Stable diagnostic command names, separate from argument parsing and private paths.
module internal DatabaseCommandTokens =
    let private copyCommand =
        function
        | DatabaseCommand.RegisterCopySigner _ -> Some "REGISTER_COPY_SIGNER"
        | DatabaseCommand.RetireCopySigner _ -> Some "RETIRE_COPY_SIGNER"
        | DatabaseCommand.IngestManagedCopy _ -> Some "INGEST_MANAGED_COPY"
        | DatabaseCommand.TransitionManagedCopy _ -> Some "TRANSITION_MANAGED_COPY"
        | DatabaseCommand.VerifyDeleteManagedCopy _ -> Some "VERIFY_DELETE_MANAGED_COPY"
        | DatabaseCommand.VerifyManagedCopy _ -> Some "VERIFY_MANAGED_COPY"
        | DatabaseCommand.AdoptManagedCopy _ -> Some "ADOPT_MANAGED_COPY"
        | DatabaseCommand.InspectManagedCopy _ -> Some "INSPECT_MANAGED_COPY"
        | DatabaseCommand.ReconcileLifecycleEvent _ -> Some "RECONCILE_LIFECYCLE_EVENT"
        | _ -> None

    let private ownerHandoff =
        function
        | DatabaseCommand.AbortWriterHandoff _ -> Some "ABORT_WRITER_HANDOFF"
        | DatabaseCommand.PrepareWriterHandoff _ -> Some "PREPARE_WRITER_HANDOFF"
        | DatabaseCommand.SettleWriterHandoff _ -> Some "SETTLE_WRITER_HANDOFF"
        | DatabaseCommand.ActivateWriterHandoff _ -> Some "ACTIVATE_WRITER_HANDOFF"
        | DatabaseCommand.DraftWriterHandoffAbort _ -> Some "DRAFT_WRITER_HANDOFF_ABORT"
        | DatabaseCommand.DraftInstallationLossRetirement _ ->
            Some "DRAFT_INSTALLATION_LOSS_RETIREMENT"
        | DatabaseCommand.RetireInstallationAfterLoss _ -> Some "RETIRE_INSTALLATION_AFTER_LOSS"
        | DatabaseCommand.ReconcileInstallationLossRetirement _ ->
            Some "RECONCILE_INSTALLATION_LOSS_RETIREMENT"
        | DatabaseCommand.VerifyRestoreReport _ -> Some "VERIFY_RESTORE_REPORT"
        | DatabaseCommand.VerifyFencedTail _ -> Some "VERIFY_FENCED_TAIL"
        | _ -> None

    let private ownerCopyAndErasure =
        function
        | DatabaseCommand.PurgeLive _ -> Some "PURGE_LIVE"
        | DatabaseCommand.PruneWitnessPayload _ -> Some "PRUNE_WITNESS_PAYLOAD"
        | DatabaseCommand.PublishExternalCopy _ -> Some "PUBLISH_EXTERNAL_COPY"
        | DatabaseCommand.TransitionAdoptedCopy _ -> Some "TRANSITION_ADOPTED_COPY"
        | DatabaseCommand.VerifyDeleteAdoptedCopy _ -> Some "VERIFY_DELETE_ADOPTED_COPY"
        | DatabaseCommand.CertifyManagedPayloadAbsence _ -> Some "CERTIFY_MANAGED_PAYLOAD_ABSENCE"
        | DatabaseCommand.CompleteSuppressionHorizon _ -> Some "COMPLETE_SUPPRESSION_HORIZON"
        | _ -> None

    let private ownerInstallation =
        function
        | DatabaseCommand.PublishRealDataActivationPlan _ ->
            Some "PUBLISH_REAL_DATA_ACTIVATION_PLAN"
        | DatabaseCommand.ActivateRealData _ -> Some "ACTIVATE_REAL_DATA"
        | DatabaseCommand.ReconcileRealDataActivation -> Some "RECONCILE_REAL_DATA_ACTIVATION"
        | DatabaseCommand.HoldBackupCapture -> Some "HOLD_BACKUP_CAPTURE"
        | DatabaseCommand.ReconcileBackupCapture _ -> Some "RECONCILE_BACKUP_CAPTURE"
        | DatabaseCommand.IssueBackupHealth _ -> Some "ISSUE_BACKUP_HEALTH"
        | DatabaseCommand.ReconcileBackupHealth _ -> Some "RECONCILE_BACKUP_HEALTH"
        | _ -> None

    let private basic =
        function
        | DatabaseCommand.Initialize _ -> "INITIALIZE"
        | DatabaseCommand.InitializeRealData _ -> "INITIALIZE_REAL_DATA"
        | DatabaseCommand.InitializeWitness -> "INITIALIZE_WITNESS"
        | DatabaseCommand.ProvisionInitialOwner -> "PROVISION_INITIAL_OWNER"
        | DatabaseCommand.Verify -> "VERIFY"
        | DatabaseCommand.VerifyData -> "VERIFY_DATA"
        | DatabaseCommand.Prune _ -> "PRUNE"
        | DatabaseCommand.Help -> "HELP"
        | DatabaseCommand.Version
        | DatabaseCommand.VersionJson -> "VERSION"
        | DatabaseCommand.Diagnostics -> "DESCRIBE_DIAGNOSTICS"
        | _ -> invalidOp "Database command classification is incomplete."

    let commandToken command =
        copyCommand command
        |> Option.orElseWith (fun () -> ownerHandoff command)
        |> Option.orElseWith (fun () -> ownerCopyAndErasure command)
        |> Option.orElseWith (fun () -> ownerInstallation command)
        |> Option.defaultWith (fun () -> basic command)
