namespace ClaimCore.Database

open System.Text.Json

/// The pre-handoff barrier proves a stable audit snapshot, not permanent writer isolation.
module internal DatabaseRestoreBarrier =
    let private fields =
        [
            "format"
            "cycleId"
            "installationId"
            "lineageId"
            "epoch"
            "backupCaptureSequence"
            "backupCaptureHash"
            "witnessCutoff"
            "witnessCutoffHash"
            "primaryWalEndpoint"
            "witnessWalEndpoint"
            "primaryCaptureWalEndpoint"
            "witnessCaptureWalEndpoint"
            "primaryRegisteredWalHorizon"
            "witnessRegisteredWalHorizon"
            "writerPaused"
            "admittedMutationsDrained"
            "primarySnapshotStable"
            "witnessCutoffStable"
            "recoveryTailUnsealed"
        ]

    let private exactClaims
        (root: JsonElement)
        (index: RestoreEvidenceIndex)
        (report: RestoreReportClaims)
        =
        let exactText = DatabaseRestoreSignedEvidence.exactText
        let exactNumber = DatabaseRestoreSignedEvidence.exactNumber
        exactText "installationId" root (report.InstallationId.ToString("D"))
        exactText "lineageId" root (report.LineageId.ToString("D"))
        exactText "cycleId" root (report.CycleId.ToString("D"))
        exactNumber "epoch" root report.Epoch
        exactNumber "backupCaptureSequence" root report.BackupCaptureSequence
        exactText "backupCaptureHash" root report.BackupCaptureHash
        exactNumber "witnessCutoff" root report.WitnessCutoff
        exactText "witnessCutoffHash" root report.WitnessCutoffHash
        exactText "primaryCaptureWalEndpoint" root index.PrimaryCaptureWalEndpoint
        exactText "witnessCaptureWalEndpoint" root index.WitnessCaptureWalEndpoint
        exactText "primaryRegisteredWalHorizon" root index.PrimaryRegisteredWalHorizon
        exactText "witnessRegisteredWalHorizon" root index.WitnessRegisteredWalHorizon
        exactText "primaryWalEndpoint" root index.PrimaryWalEndpoint
        exactText "witnessWalEndpoint" root index.WitnessWalEndpoint

    let verify (index: RestoreEvidenceIndex) (report: RestoreReportClaims) (reportKey: byte array) =
        use document =
            DatabaseRestoreSignedEvidence.verifyFile
                32768
                report.QuiescentBarrierSha256
                reportKey
                index.BarrierFile
                index.BarrierSignatureFile

        let root = document.RootElement

        if not (DatabaseRestoreCanonical.exactProperties fields root) then
            invalidOp "Signed quiescent audit barrier has a changed field set."

        exactClaims root index report

        let flag = DatabaseRestoreCanonical.flag
        let format = DatabaseRestoreCanonical.text "format" root

        if
            format <> "claimcore-quiescent-audit-barrier-1"
            || not (flag "writerPaused" root)
            || not (flag "admittedMutationsDrained" root)
            || not (flag "primarySnapshotStable" root)
            || not (flag "witnessCutoffStable" root)
            || not (flag "recoveryTailUnsealed" root)
        then
            invalidOp "Signed quiescent audit barrier is incomplete."
