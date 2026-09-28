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
    let private walPosition (value: string) =
        let parts = value.Split('/')

        if parts.Length <> 2 then
            invalidOp "Restore WAL endpoint is invalid."

        let high =
            UInt64.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture)

        let low =
            UInt64.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture)

        if high > 0xFFFFFFFFUL || low > 0xFFFFFFFFUL then
            invalidOp "Restore WAL endpoint is invalid."

        (high <<< 32) ||| low

    let matchesLivePair
        (publication: TrustedRestorePublication)
        (report: RestoreReportClaims)
        (index: RestoreEvidenceIndex)
        (facts: RestoredPairFacts)
        binarySha256
        =
        publication.ManifestSha256 = index.PublicationManifestSha256
        && publication.VerifierBinarySha256 = binarySha256
        && report.VerifierBinarySha256 = binarySha256
        && publication.ReportSignerKeyId = report.SignerKeyId
        && publication.CheckpointSignerKeyId = index.CheckpointSignerKeyId
        && publication.ReportSignerKeyId <> publication.CheckpointSignerKeyId
        && publication.InstallationId = facts.InstallationId
        && publication.LineageId = facts.LineageId
        && publication.Epoch = facts.Epoch
        && publication.WriterGeneration = facts.WriterGeneration
        && publication.WitnessCutoff = facts.WitnessCutoff
        && publication.WitnessCutoffHash = facts.WitnessCutoffHash
        && report.InstallationId = facts.InstallationId
        && report.LineageId = facts.LineageId
        && report.Epoch = facts.Epoch
        && report.AuthorityRevision = facts.AuthorityRevision
        && report.PrimarySystemId = facts.PrimarySystemId
        && report.PrimaryTimeline = facts.PrimaryTimeline
        && report.WitnessSystemId = facts.WitnessSystemId
        && report.WitnessTimeline = facts.WitnessTimeline
        && report.WitnessCutoff = facts.WitnessCutoff
        && report.WitnessCutoffHash = facts.WitnessCutoffHash
        && report.CatalogManifestSha256 = facts.CatalogManifestSha256
        && facts.PendingIntents = 0L
        && walPosition facts.PrimaryWalEndpoint >= walPosition index.PrimaryWalEndpoint
        && walPosition facts.WitnessWalEndpoint >= walPosition index.WitnessWalEndpoint

    let internal signedEvidence
        barrier
        transaction
        witness
        (files: RestoreReportFiles)
        (report: RestoreReportClaims)
        (index: RestoreEvidenceIndex)
        (facts: RestoredPairFacts)
        =
        let reportKey, reportHolder =
            DatabaseRestoreSignedEvidence.signer
                barrier
                transaction
                report.SignerKeyId
                CopySignerPurpose.RestoreReport

        let checkpointKey, checkpointHolder =
            DatabaseRestoreSignedEvidence.signer
                barrier
                transaction
                index.CheckpointSignerKeyId
                CopySignerPurpose.Checkpoint

        try
            DatabaseRestoreWalCoverage.verify index report

            if
                CryptographicOperations.FixedTimeEquals(reportKey, checkpointKey)
                || reportHolder = checkpointHolder
                || not (DatabaseRestoreSignedEvidence.checkpointMatches report index checkpointKey)
            then
                invalidOp "Restored report and checkpoint custody proof diverges."

            DatabaseRestoreSignedEvidence.verifyReport reportKey files

            let primaryCopy, witnessCopy =
                DatabaseRestoreArtifacts.verifyManifest index report reportKey

            DatabaseRestoreCopyEvidence.verify barrier transaction report primaryCopy witnessCopy

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
            DatabaseRestoreCheckpoint.verify index report checkpointKey witness
        finally
            CryptographicOperations.ZeroMemory(reportKey)
            CryptographicOperations.ZeroMemory(checkpointKey)

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
        try
            let _, _, facts =
                DatabaseVerifyData.auditedRestoredWith
                    ownerConnection
                    witnessAuditConnection
                    custody
                    suppressionKey
                    (fun barrier transaction witness audit tip ->
                        let facts =
                            DatabaseRestoreLive.inspect
                                barrier
                                transaction
                                witnessOwnerConnection
                                witness
                                audit
                                tip

                        if not (matchesLivePair trusted report index facts binarySha256) then
                            invalidOp "Restored live evidence differs from report."

                        signedEvidence barrier transaction witness files report index facts
                        facts)

            Ok
                {
                    Nonce = nonce
                    ReportSha256 = files.ReportSha256
                    EvidenceIndexSha256 = files.EvidenceIndexSha256
                    WitnessCutoff = facts.WitnessCutoff
                    WitnessCutoffHash = facts.WitnessCutoffHash
                }
        with _ ->
            Error RestoreRecheckFailure.EvidenceMismatch

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
        let validNonce =
            nonce.Length = 64
            && nonce
               |> Seq.forall (fun character ->
                   ('0' <= character && character <= '9') || ('a' <= character && character <= 'f'))

        match publication with
        | None -> Error RestoreRecheckFailure.TrustAnchorUnavailable
        | Some _ when not validNonce -> Error RestoreRecheckFailure.EvidenceMismatch
        | Some trusted ->
            match DatabaseRestoreReportClaims.parse files.Report now with
            | None -> Error RestoreRecheckFailure.ReportInvalid
            | Some report when report.Scope <> requiredScope ->
                Error RestoreRecheckFailure.ReportInvalid
            | Some report when report.EvidenceIndexSha256 <> files.EvidenceIndexSha256 ->
                Error RestoreRecheckFailure.EvidenceIndexInvalid
            | Some report ->
                match DatabaseRestoreEvidenceIndex.parse files.EvidenceIndex report with
                | None -> Error RestoreRecheckFailure.EvidenceIndexInvalid
                | Some index ->
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
        try
            let primary = OwnerConnection.builder ownerConnection
            let witness = NpgsqlConnectionStringBuilder(witnessAuditConnection)
            let primaryName = primary.Database |> Option.ofObj |> Option.defaultValue ""
            let witnessName = witness.Database |> Option.ofObj |> Option.defaultValue ""
            let primaryHost = primary.Host |> Option.ofObj |> Option.defaultValue ""
            let witnessHost = witness.Host |> Option.ofObj |> Option.defaultValue ""

            let local host =
                host = "127.0.0.1" || host = "localhost" || host = "::1"

            if
                not (primaryName.EndsWith("_test", StringComparison.Ordinal))
                || not (witnessName.EndsWith("_test", StringComparison.Ordinal))
                || not (local primaryHost && local witnessHost)
                || primary.Port = witness.Port
            then
                Error RestoreRecheckFailure.EvidenceMismatch
            else
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
            Error RestoreRecheckFailure.EvidenceMismatch
