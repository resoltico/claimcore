namespace ClaimCore.Database

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres

/// Fixed, precomputed evidence for one exact owner command. It never opens a second
/// primary connection while the Postgres handoff method holds its authority lock.
module internal DatabaseWriterHandoffQualification =
    let private digest (bytes: byte array) =
        SHA256.HashData(ReadOnlySpan<byte>(bytes))

    let private currentFenceSigner
        (ownerConnection: string)
        (report: RestoreReportClaims)
        (index: RestoreEvidenceIndex)
        (fenceBytes: byte array)
        (fenceSignature: byte array)
        =
        use owner = new NpgsqlConnection(ownerConnection)
        owner.Open()
        use transaction = owner.BeginTransaction()

        let reportKey, reportHolder =
            DatabaseRestoreSignedEvidence.signer
                owner
                transaction
                report.SignerKeyId
                CopySignerPurpose.RestoreReport

        try
            let checkpointKey, checkpointHolder =
                DatabaseRestoreSignedEvidence.signer
                    owner
                    transaction
                    index.CheckpointSignerKeyId
                    CopySignerPurpose.Checkpoint

            try
                if
                    reportHolder = checkpointHolder
                    || CryptographicOperations.FixedTimeEquals(reportKey, checkpointKey)
                    || not (ManagedCopySignature.verify checkpointKey fenceBytes fenceSignature)
                then
                    invalidOp "Old-writer fence lacks an independent current human signer."
            finally
                CryptographicOperations.ZeroMemory(checkpointKey)
        finally
            CryptographicOperations.ZeroMemory(reportKey)

        transaction.Rollback()

    let private fence
        owner
        (proposal: WriterHandoffPreparation)
        (reviewed: HandoffReportEvidence)
        (fenceBytes: byte array)
        (fenceSignature: byte array)
        now
        =
        let claims =
            DatabaseRestoreWriterFenceClaims.parse fenceBytes now
            |> Option.defaultWith (fun () -> invalidOp "Signed writer fence is invalid.")

        let fenceSha = digest fenceBytes

        if
            proposal.FenceReportSha256 <> fenceSha
            || claims.ReportSha256 <> reviewed.ReportSha256
            || claims.InstallationId <> proposal.InstallationId
            || claims.LineageId <> proposal.LineageId
            || claims.Epoch <> proposal.Epoch
            || claims.OldGeneration <> proposal.OldGeneration
            || claims.NewGeneration <> proposal.NewGeneration
            || claims.CheckpointSignerKeyId <> proposal.CheckpointSigningKeyId
            || claims.ValidUntil < proposal.ValidUntil
        then
            invalidOp "Signed old-writer fence differs from exact W1 proposal."

        currentFenceSigner owner reviewed.Report reviewed.Index fenceBytes fenceSignature
        claims

    let private proof
        (proposal: WriterHandoffPreparation)
        (reviewed: HandoffReportEvidence)
        (fence: WriterFenceClaims)
        =
        {
            PublicationManifestSha256 = Convert.FromHexString(reviewed.Publication.ManifestSha256)
            InstallationId = proposal.InstallationId
            LineageId = proposal.LineageId
            Epoch = proposal.Epoch
            OldGeneration = proposal.OldGeneration
            ReviewedCutoffSequence = proposal.ReviewedCutoffSequence
            ReviewedCutoffHash = Array.copy proposal.ReviewedCutoffHash
            NewCapabilitySha256 = Array.copy proposal.NewCapabilitySha256
            RestoreReportSha256 = Array.copy proposal.RestoreReportSha256
            FenceReportSha256 = Array.copy proposal.FenceReportSha256
            InventorySha256 = Array.copy proposal.InventorySha256
            CheckpointSigningKeyId = proposal.CheckpointSigningKeyId
            ValidUntil = min proposal.ValidUntil fence.ValidUntil
            IndependentProbeNonceSha256 = Convert.FromHexString(fence.IndependentProbeSha256)
        }

    let private fixedVerifier
        (qualified: WriterHandoffQualifiedEvidence)
        (exactCanonical: byte array)
        (stage: string)
        : IWriterHandoffEvidenceVerifier =
        let canonicalSha = digest exactCanonical

        let matches canonical selected =
            selected = stage
            && CryptographicOperations.FixedTimeEquals(digest canonical, canonicalSha)

        { new IWriterHandoffEvidenceVerifier with
            member _.VerifyPreparation(_, canonical, _: CancellationToken) =
                Task.FromResult(if matches canonical "PREPARE" then Some qualified else None)

            member _.VerifySettlement(_, canonical, _: CancellationToken) =
                Task.FromResult(if matches canonical "COMMIT" then Some qualified else None)
        }

    let preparation
        publication
        scope
        owner
        witnessAudit
        witnessOwner
        custody
        suppression
        files
        (proposal: WriterHandoffPreparation)
        canonical
        fenceBytes
        fenceSignature
        binarySha256
        now
        =
        let reviewed =
            DatabaseRestoreHandoffRecheck.preparation
                publication
                scope
                owner
                witnessAudit
                witnessOwner
                custody
                suppression
                files
                proposal
                binarySha256
                now

        let attestation = fence owner proposal reviewed fenceBytes fenceSignature now

        proof proposal reviewed attestation
        |> fun value -> fixedVerifier value canonical "PREPARE"

    let settlement
        publication
        scope
        owner
        witnessAudit
        witnessOwner
        custody
        suppression
        files
        (prepared: PrimaryWriterPreparation)
        canonical
        fenceBytes
        fenceSignature
        binarySha256
        now
        =
        let reviewed =
            DatabaseRestoreHandoffRecheck.settlement
                publication
                scope
                owner
                witnessAudit
                witnessOwner
                custody
                suppression
                files
                prepared
                binarySha256
                now

        let attestation = fence owner prepared.Value reviewed fenceBytes fenceSignature now

        proof prepared.Value reviewed attestation
        |> fun value -> fixedVerifier value canonical "COMMIT"
