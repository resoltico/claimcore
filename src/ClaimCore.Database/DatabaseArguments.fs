namespace ClaimCore.Database

open System
open System.Globalization
open ClaimCore.Postgres
open ClaimCore.Application

module DatabaseArguments =
    let private signerPurpose =
        function
        | "COPY_ATTESTOR" -> Some CopySignerPurpose.CopyAttestor
        | "LOCATION_REGISTRY" -> Some CopySignerPurpose.LocationRegistry
        | "LOCATION_INSPECTOR" -> Some CopySignerPurpose.LocationInspector
        | "DELETION_VERIFIER" -> Some CopySignerPurpose.DeletionVerifier
        | "RESTORE_REPORT" -> Some CopySignerPurpose.RestoreReport
        | "CHECKPOINT" -> Some CopySignerPurpose.Checkpoint
        | "WRITER_HANDOFF_ABORT" -> Some CopySignerPurpose.WriterHandoffAbort
        | "INSTALLATION_LOSS_RETIREMENT" -> Some CopySignerPurpose.InstallationLossRetirement
        | "RESTORE_COPY_VERIFIER" -> Some CopySignerPurpose.RestoreCopyVerifier
        | _ -> None

    let private exactGuid (value: string) =
        match Guid.TryParseExact(value, "D") with
        | true, parsed when parsed <> Guid.Empty && parsed.ToString("D") = value -> Some parsed
        | _ -> None

    let private update options setting value =
        match setting with
        | DatabaseOption.SettledRetention ->
            { options with
                SettledRetentionDays = value
            }
        | DatabaseOption.AbandonedRetention ->
            { options with
                AbandonedRetentionDays = value
            }
        | DatabaseOption.Limit -> { options with BatchLimit = value }
        | DatabaseOption.DryRun -> { options with DryRun = true }

    let private numeric setting (raw: string) =
        match Int32.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, value when value >= 1 && value <= DatabaseOptions.maximum setting -> Ok value
        | _ -> Error(DatabaseInputProblem.OptionOutOfRange setting)

    let private prune arguments =
        let rec read options seen remaining =
            match remaining with
            | [] -> Ok(DatabaseCommand.Prune options)
            | raw :: tail ->
                match DatabaseOptions.all |> List.tryFind (DatabaseOptions.token >> (=) raw) with
                | None -> Error DatabaseInputProblem.UnknownOption
                | Some setting when Set.contains setting seen ->
                    Error(DatabaseInputProblem.RepeatedOption setting)
                | Some DatabaseOption.DryRun ->
                    read { options with DryRun = true } (Set.add DatabaseOption.DryRun seen) tail
                | Some setting ->
                    match tail with
                    | [] -> Error(DatabaseInputProblem.MissingOptionValue setting)
                    | value :: _ when value.StartsWith("--", StringComparison.Ordinal) ->
                        Error(DatabaseInputProblem.MissingOptionValue setting)
                    | value :: rest ->
                        numeric setting value
                        |> Result.bind (fun number ->
                            read (update options setting number) (Set.add setting seen) rest)

        read PreparationPruneOptions.defaults Set.empty arguments

    let private basic =
        function
        | [ "help" ]
        | [ "--help" ] -> Some(Ok DatabaseCommand.Help)
        | [ "version" ]
        | [ "--version" ] -> Some(Ok DatabaseCommand.Version)
        | [ "version"; "--json" ] -> Some(Ok DatabaseCommand.VersionJson)
        | [ "describe"; "diagnostics" ] -> Some(Ok DatabaseCommand.Diagnostics)
        | [ "verify" ] -> Some(Ok DatabaseCommand.Verify)
        | [ "verify-data" ] -> Some(Ok DatabaseCommand.VerifyData)
        | _ -> None

    let private installation =
        function
        | [ "initialize"; zone ] -> Some(Ok(DatabaseCommand.Initialize zone))
        | [ "initialize-real-data"; zone ] -> Some(Ok(DatabaseCommand.InitializeRealData zone))
        | [ "publish-real-data-activation-plan"; policy; evidence; output ] ->
            Some(Ok(DatabaseCommand.PublishRealDataActivationPlan(policy, evidence, output)))
        | [ "activate-real-data"; policy; original; fresh; plan; first; second ] ->
            match exactGuid plan, exactGuid first, exactGuid second with
            | Some planId, Some firstId, Some secondId when firstId <> secondId ->
                Some(
                    Ok(
                        DatabaseCommand.ActivateRealData(
                            policy,
                            original,
                            fresh,
                            planId,
                            firstId,
                            secondId
                        )
                    )
                )
            | _ -> Some(Error DatabaseInputProblem.UnsupportedInvocation)
        | [ "reconcile-real-data-activation" ] ->
            Some(Ok DatabaseCommand.ReconcileRealDataActivation)
        | [ "initialize-witness" ] -> Some(Ok DatabaseCommand.InitializeWitness)
        | [ "provision-initial-owner" ] -> Some(Ok DatabaseCommand.ProvisionInitialOwner)
        | _ -> None

    let private backupCapture =
        function
        | [ "hold-backup-capture" ] -> Some(Ok DatabaseCommand.HoldBackupCapture)
        | [ "reconcile-backup-capture"; lease ] ->
            match exactGuid lease with
            | Some value -> Some(Ok(DatabaseCommand.ReconcileBackupCapture value))
            | None -> Some(Error DatabaseInputProblem.UnsupportedInvocation)
        | _ -> None

    let private registerSigner =
        function
        | [ "register-copy-signer"
            purpose
            eventId
            keyId
            publicKey
            ownerApproval
            custodianApproval ] ->
            match
                signerPurpose purpose,
                exactGuid eventId,
                exactGuid keyId,
                exactGuid ownerApproval,
                exactGuid custodianApproval
            with
            | Some purposeValue,
              Some eventValue,
              Some keyValue,
              Some ownerValue,
              Some custodianValue when ownerValue <> custodianValue ->
                Some(
                    Ok(
                        DatabaseCommand.RegisterCopySigner(
                            purposeValue,
                            eventValue,
                            keyValue,
                            publicKey,
                            ownerValue,
                            custodianValue
                        )
                    )
                )
            | _ -> Some(Error DatabaseInputProblem.UnsupportedInvocation)
        | _ -> None

    let private retireSigner =
        function
        | [ "retire-copy-signer"; purpose; eventId; keyId; ownerApproval; custodianApproval ] ->
            match
                signerPurpose purpose,
                exactGuid eventId,
                exactGuid keyId,
                exactGuid ownerApproval,
                exactGuid custodianApproval
            with
            | Some purposeValue,
              Some eventValue,
              Some keyValue,
              Some ownerValue,
              Some custodianValue when ownerValue <> custodianValue ->
                Some(
                    Ok(
                        DatabaseCommand.RetireCopySigner(
                            purposeValue,
                            eventValue,
                            keyValue,
                            ownerValue,
                            custodianValue
                        )
                    )
                )
            | _ -> Some(Error DatabaseInputProblem.UnsupportedInvocation)
        | _ -> None

    let private signer arguments =
        match registerSigner arguments with
        | Some result -> Some result
        | None -> retireSigner arguments

    let private managedCopy =
        function
        | [ "ingest-managed-copy"; attestation; signature ] ->
            Some(Ok(DatabaseCommand.IngestManagedCopy(attestation, signature)))
        | [ "transition-managed-copy"; attestation; signature ] ->
            Some(Ok(DatabaseCommand.TransitionManagedCopy(attestation, signature)))
        | [ "verify-delete-managed-copy"; attestation; signature ] ->
            Some(Ok(DatabaseCommand.VerifyDeleteManagedCopy(attestation, signature)))
        | [ "verify-managed-copy"; attestation; signature ] ->
            Some(Ok(DatabaseCommand.VerifyManagedCopy(attestation, signature)))
        | [ "adopt-managed-copy"; proposal ] -> Some(Ok(DatabaseCommand.AdoptManagedCopy proposal))
        | [ "publish-external-copy"; proposal ] ->
            Some(Ok(DatabaseCommand.PublishExternalCopy proposal))
        | _ -> None

    let private handoff =
        function
        | [ "abort-writer-handoff"; candidate; signatureOne; signatureTwo ] ->
            Some(Ok(DatabaseCommand.AbortWriterHandoff(candidate, signatureOne, signatureTwo)))
        | [ "prepare-writer-handoff"; canonical; signature ] ->
            Some(Ok(DatabaseCommand.PrepareWriterHandoff(canonical, signature)))
        | [ "settle-writer-handoff"; canonical; signature ] ->
            Some(Ok(DatabaseCommand.SettleWriterHandoff(canonical, signature)))
        | [ "draft-writer-handoff-abort"; handoff; keyOne; keyTwo; output ] ->
            match exactGuid handoff, exactGuid keyOne, exactGuid keyTwo with
            | Some handoffId, Some first, Some second when first <> second ->
                Some(Ok(DatabaseCommand.DraftWriterHandoffAbort(handoffId, first, second, output)))
            | _ -> Some(Error DatabaseInputProblem.UnsupportedInvocation)
        | _ -> None

    let private adoptedCopy =
        function
        | [ "transition-adopted-copy"; canonical; signature ] ->
            Some(Ok(DatabaseCommand.TransitionAdoptedCopy(canonical, signature)))
        | [ "verify-delete-adopted-copy"; canonical; signature ] ->
            Some(Ok(DatabaseCommand.VerifyDeleteAdoptedCopy(canonical, signature)))
        | _ -> None

    let private erasure =
        function
        | [ "purge-live"; proposal ] -> Some(Ok(DatabaseCommand.PurgeLive proposal))
        | [ "prune-witness-payload"; proposal ] ->
            Some(Ok(DatabaseCommand.PruneWitnessPayload proposal))
        | [ "certify-managed-payload-absence"; proposal ] ->
            Some(Ok(DatabaseCommand.CertifyManagedPayloadAbsence proposal))
        | [ "complete-suppression-horizon"; proposal ] ->
            Some(Ok(DatabaseCommand.CompleteSuppressionHorizon proposal))
        | _ -> None

    let private otherManaged =
        function
        | [ "issue-backup-health"; policy; evidence; output ] ->
            Some(Ok(DatabaseCommand.IssueBackupHealth(policy, evidence, output)))
        | [ "reconcile-backup-health"; policy; evidence; output ] ->
            Some(Ok(DatabaseCommand.ReconcileBackupHealth(policy, evidence, output)))
        | [ "inspect-managed-copy"; copyId ] ->
            match exactGuid copyId with
            | Some value -> Some(Ok(DatabaseCommand.InspectManagedCopy value))
            | None -> Some(Error DatabaseInputProblem.UnsupportedInvocation)
        | [ "reconcile-lifecycle-event"; eventId ] ->
            match exactGuid eventId with
            | Some value -> Some(Ok(DatabaseCommand.ReconcileLifecycleEvent value))
            | None -> Some(Error DatabaseInputProblem.UnsupportedInvocation)
        | _ -> None

    let private managed arguments =
        managedCopy arguments
        |> Option.orElseWith (fun () -> handoff arguments)
        |> Option.orElseWith (fun () -> DatabaseLossArguments.parse arguments)
        |> Option.orElseWith (fun () -> adoptedCopy arguments)
        |> Option.orElseWith (fun () -> erasure arguments)
        |> Option.orElseWith (fun () -> otherManaged arguments)
        |> Option.orElseWith (fun () -> DatabaseRestoreArguments.parse arguments)

    let parse arguments =
        match basic arguments with
        | Some parsed -> parsed
        | None ->
            installation arguments
            |> Option.orElseWith (fun () -> backupCapture arguments)
            |> Option.orElseWith (fun () -> signer arguments)
            |> Option.orElseWith (fun () -> managed arguments)
            |> Option.defaultWith (fun () ->
                match arguments with
                | "prune" :: options -> prune options
                | _ -> Error DatabaseInputProblem.UnsupportedInvocation)

    let commandToken = DatabaseCommandTokens.commandToken
