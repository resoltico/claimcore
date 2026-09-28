namespace ClaimCore.Database

open System.IO

module internal DatabaseOwnerPrivateExecution =
    let private present deliver fail =
        function
        | Error reason -> fail reason
        | Ok result -> deliver result

    let private erasure connection command deliver fail =
        match command with
        | DatabaseCommand.PurgeLive proposal ->
            DatabasePurgeExecution.run connection proposal |> present deliver fail |> Some
        | DatabaseCommand.PruneWitnessPayload proposal ->
            DatabasePruneWitnessExecution.run connection proposal
            |> present deliver fail
            |> Some
        | DatabaseCommand.CertifyManagedPayloadAbsence proposal ->
            DatabaseTerminalCopyAbsenceExecution.run connection proposal
            |> present deliver fail
            |> Some
        | DatabaseCommand.CompleteSuppressionHorizon proposal ->
            DatabaseTerminalCopyAbsenceExecution.runFinal connection proposal
            |> present deliver fail
            |> Some
        | _ -> None

    let private handoff connection command deliver fail =
        match command with
        | DatabaseCommand.PrepareWriterHandoff(canonical, signature) ->
            DatabaseWriterHandoffExecution.run connection canonical signature "PREPARE"
            |> present deliver fail
            |> Some
        | DatabaseCommand.SettleWriterHandoff(canonical, signature) ->
            DatabaseWriterHandoffExecution.run connection canonical signature "COMMIT"
            |> present deliver fail
            |> Some
        | DatabaseCommand.ActivateWriterHandoff paths ->
            DatabaseFencedTailOwnerExecution.activate connection paths
            |> present deliver fail
            |> Some
        | DatabaseCommand.AbortWriterHandoff(candidate, signatureOne, signatureTwo) ->
            DatabaseWriterHandoffAbortExecution.run connection candidate signatureOne signatureTwo
            |> present deliver fail
            |> Some
        | DatabaseCommand.DraftWriterHandoffAbort(handoffId, keyOne, keyTwo, outputFile) ->
            DatabaseWriterHandoffAbortDraftExecution.run
                connection
                handoffId
                keyOne
                keyTwo
                outputFile
            |> present deliver fail
            |> Some
        | _ -> None

    let private dataUse connection command deliver fail =
        match command with
        | DatabaseCommand.PublishRealDataActivationPlan(policy, evidence, output) ->
            DatabaseRealDataPlanExecution.run connection policy evidence output
            |> present deliver fail
            |> Some
        | DatabaseCommand.ActivateRealData(policy, original, fresh, planId, first, second) ->
            DatabaseRealDataActivationExecution.activate
                connection
                policy
                original
                fresh
                planId
                first
                second
            |> present deliver fail
            |> Some
        | DatabaseCommand.ReconcileRealDataActivation ->
            DatabaseRealDataActivationExecution.reconcile connection
            |> present deliver fail
            |> Some
        | _ -> None

    let private verification connection command (output: Stream) (errors: Stream) =
        match command with
        | DatabaseCommand.VerifyRestoreReport(report, signature, index, nonce) ->
            DatabaseRestoreReportExecution.run connection report signature index nonce output errors
            |> Some
        | DatabaseCommand.VerifyFencedTail(paths, nonce) ->
            DatabaseFencedTailOwnerExecution.verify connection paths nonce output errors
            |> Some
        | DatabaseCommand.ReconcileBackupHealth(policy, evidence, certificate) ->
            DatabaseBackupHealthReconciliation.run
                connection
                policy
                evidence
                certificate
                output
                errors
            |> Some
        | _ -> None

    let private backup connection command deliver fail =
        match command with
        | DatabaseCommand.IssueBackupHealth(policy, evidence, certificate) ->
            DatabaseBackupHealthExecution.run connection policy evidence certificate
            |> present deliver fail
            |> Some
        | DatabaseCommand.HoldBackupCapture ->
            DatabaseBackupCaptureExecution.run connection |> deliver |> Some
        | DatabaseCommand.ReconcileBackupCapture leaseId ->
            DatabaseBackupCaptureExecution.reconcile connection leaseId |> deliver |> Some
        | _ -> None

    let route connection command (output: Stream) (errors: Stream) deliver fail =
        erasure connection command deliver fail
        |> Option.orElseWith (fun () -> handoff connection command deliver fail)
        |> Option.orElseWith (fun () -> dataUse connection command deliver fail)
        |> Option.orElseWith (fun () -> verification connection command output errors)
        |> Option.orElseWith (fun () -> backup connection command deliver fail)
