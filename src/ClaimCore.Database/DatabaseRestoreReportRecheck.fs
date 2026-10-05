namespace ClaimCore.Database

open System
open System.Globalization
open System.Security.Cryptography
open ClaimCore.HostSecurity
open ClaimCore.Witness
open ClaimCore.Application
open Npgsql
open ClaimCore.Postgres

module internal DatabaseRestoreReportRecheck =
    let internal signedEvidence
        barrier
        transaction
        witness
        (files: RestoreReportFiles)
        (report: RestoreReportClaims)
        (index: RestoreEvidenceIndex)
        (facts: RestoredPairFacts)
        =
        task {
            let reportKey, reportHolder, checkpointKey, checkpointHolder =
                DatabaseRestoreSignedEvidence.signerPair barrier transaction report index

            try
                DatabaseRestoreWalCoverage.verify index report

                if
                    CryptographicOperations.FixedTimeEquals(reportKey, checkpointKey)
                    || reportHolder = checkpointHolder
                    || not (
                        DatabaseRestoreSignedEvidence.checkpointMatches report index checkpointKey
                    )
                then
                    invalidOp "Restored report and checkpoint custody proof diverges."

                DatabaseRestoreSignedEvidence.verifyReport reportKey files

                let primaryCopy, witnessCopy =
                    DatabaseRestoreArtifacts.verifyManifest index report reportKey

                DatabaseRestoreCopyEvidence.verify
                    barrier
                    transaction
                    report
                    primaryCopy
                    witnessCopy

                DatabaseRestoreArtifacts.verifyArchive
                    barrier
                    transaction
                    index
                    report
                    primaryCopy
                    witnessCopy

                DatabaseRestoreOwnerRoster.verify barrier transaction report.AuthorizedApprovers
                DatabaseRestoreBarrier.verify index report reportKey
                DatabaseRestoreArtifacts.verifyInventory index report checkpointKey facts
                do! DatabaseRestoreCheckpoint.verify index report checkpointKey witness
            finally
                CryptographicOperations.ZeroMemory(reportKey)
                CryptographicOperations.ZeroMemory(checkpointKey)
        }

    let private inspectCurrent
        trusted
        report
        index
        binarySha256
        witnessOwnerConnection
        files
        barrier
        transaction
        witness
        audit
        tip
        =
        task {
            let! facts =
                DatabaseRestoreLive.inspect
                    barrier
                    transaction
                    witnessOwnerConnection
                    witness
                    audit
                    tip

            if
                not (DatabaseRestoreLive.matchesLivePair trusted report index facts binarySha256)
            then
                invalidOp "Restored live evidence differs from report."

            do! signedEvidence barrier transaction witness files report index facts

            return facts
        }

    let private recompute
        trusted
        ownerConnection
        witnessAuditConnection
        witnessOwnerConnection
        custody
        suppressionKey
        (files: RestoreReportFiles)
        (report: RestoreReportClaims)
        index
        nonce
        binarySha256
        =
        task {
            try
                let! _, _, facts =
                    DatabaseVerifyData.auditedRestoredWith
                        ownerConnection
                        witnessAuditConnection
                        custody
                        suppressionKey
                        (inspectCurrent
                            trusted
                            report
                            index
                            binarySha256
                            witnessOwnerConnection
                            files)

                return
                    Ok
                        {
                            Nonce = nonce
                            ReportSha256 = files.ReportSha256
                            EvidenceIndexSha256 = files.EvidenceIndexSha256
                            WitnessCutoff = facts.WitnessCutoff
                            WitnessCutoffHash = facts.WitnessCutoffHash
                        }
            with _ ->
                return Error RestoreRecheckFailure.EvidenceMismatch
        }

    let private evaluateForScope
        requiredScope
        (publication: TrustedRestorePublication option)
        ownerConnection
        witnessAuditConnection
        witnessOwnerConnection
        (custody: IKeyCustody)
        (suppressionKey: SuppressionKeyFile)
        (files: RestoreReportFiles)
        (nonce: string)
        binarySha256
        now
        =
        task {
            let validNonce =
                nonce.Length = 64
                && nonce
                   |> Seq.forall (fun character ->
                       ('0' <= character && character <= '9')
                       || ('a' <= character && character <= 'f'))

            match publication with
            | None -> return Error RestoreRecheckFailure.TrustAnchorUnavailable
            | Some _ when not validNonce -> return Error RestoreRecheckFailure.EvidenceMismatch
            | Some trusted ->
                match DatabaseRestoreReportClaims.parse files.Report now with
                | None -> return Error RestoreRecheckFailure.ReportInvalid
                | Some report when report.Scope <> requiredScope ->
                    return Error RestoreRecheckFailure.ReportInvalid
                | Some report when report.EvidenceIndexSha256 <> files.EvidenceIndexSha256 ->
                    return Error RestoreRecheckFailure.EvidenceIndexInvalid
                | Some report ->
                    match DatabaseRestoreEvidenceIndex.parse files.EvidenceIndex report with
                    | None -> return Error RestoreRecheckFailure.EvidenceIndexInvalid
                    | Some index ->
                        return!
                            recompute
                                trusted
                                ownerConnection
                                witnessAuditConnection
                                witnessOwnerConnection
                                custody
                                suppressionKey
                                files
                                report
                                index
                                nonce
                                binarySha256

        /// Product rechecks never accept a synthetic-only report, even with a valid detached signature.
        }

    let evaluate
        publication
        ownerConnection
        witnessAuditConnection
        witnessOwnerConnection
        custody
        suppressionKey
        files
        nonce
        binarySha256
        now
        =
        evaluateForScope
            "full"
            publication
            ownerConnection
            witnessAuditConnection
            witnessOwnerConnection
            custody
            suppressionKey
            files
            nonce
            binarySha256
            now

    /// Isolated qualification only. No ordinary Database command calls this entry point.
    let evaluateSynthetic
        publication
        (ownerConnection: string)
        (witnessAuditConnection: string)
        witnessOwnerConnection
        custody
        suppressionKey
        files
        nonce
        binarySha256
        now
        =
        task {
            try
                DatabaseRestoreIsolation.requireIsolated ownerConnection witnessAuditConnection

                return!
                    evaluateForScope
                        "synthetic-only"
                        publication
                        ownerConnection
                        witnessAuditConnection
                        witnessOwnerConnection
                        custody
                        suppressionKey
                        files
                        nonce
                        binarySha256
                        now
            with _ ->
                return Error RestoreRecheckFailure.EvidenceMismatch
        }
