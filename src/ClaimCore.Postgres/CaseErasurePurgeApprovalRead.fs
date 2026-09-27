namespace ClaimCore.Postgres

open System
open System.IO
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application

/// Owner-only consumption of two existing human approvals; no supplied actor IDs are trusted.
module internal CaseErasurePurgeApprovalRead =
    let private corrupt () : 'a =
        raise (InvalidDataException("Erasure purge approval evidence differs."))

    let private exactApproval
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        (draftHash: byte array)
        (approval: LifecycleApprovalEvidence)
        (stored: LifecycleStoredApproval)
        =
        let exact =
            CaseLifecycleCandidate.approval
                approval.ApprovalId
                change.EventId
                projection.CaseId
                draftHash
                approval.ApproverId
                stored.GrantRevision
                stored.ApprovedAt
                approval.ExpiresAt

        try
            if
                stored.OperationId <> change.EventId
                || stored.CaseId <> projection.CaseId
                || stored.DraftHash <> draftHash
                || stored.ApproverId <> approval.ApproverId
                || stored.ExpiresAt <> approval.ExpiresAt
                || stored.Canonical <> exact
                || stored.CandidateHash <> SHA256.HashData(exact)
                || stored.GrantRevision < 1L
            then
                corrupt ()
        finally
            CryptographicOperations.ZeroMemory(exact)

    let private receipt
        (commitments: ISuppressionCommitments)
        (draftCommitment: byte array)
        (approval: LifecycleApprovalEvidence)
        (stored: LifecycleStoredApproval)
        =
        {
            ApprovalId = approval.ApprovalId
            ApproverId = stored.ApproverId
            GrantRevision = stored.GrantRevision
            ApprovedAt = stored.ApprovedAt
            ExpiresAt = stored.ExpiresAt
            DraftSha256 = stored.DraftHash
            DraftCommitment = draftCommitment
            Canonical = stored.Canonical
            CandidateSha256 = stored.CandidateHash
            ApprovalCommitment = commitments.ApprovalCanonical(stored.Canonical)
            WitnessSequence = stored.WitnessSequence
            WitnessEpoch = stored.WitnessEpoch
            WitnessHash = stored.WitnessHash
        }

    let private row
        connection
        transaction
        (witness: WitnessProtocol)
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        (draftHash: byte array)
        (draftCommitment: byte array)
        (commitments: ISuppressionCommitments)
        (approval: LifecycleApprovalEvidence)
        =
        task {
            let! found =
                CaseLifecycleRead.approvalById connection transaction approval.ApprovalId

            let stored = found |> Option.defaultWith corrupt
            exactApproval projection change draftHash approval stored

            witness.VerifyAuthorityEvidenceForCase(
                approval.ApprovalId,
                stored.WitnessSequence,
                stored.WitnessEpoch,
                stored.WitnessHash,
                stored.CandidateHash,
                projection.CaseId
            )

            return receipt commitments draftCommitment approval stored
        }

    let read
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        (draftHash: byte array)
        (canonicalDraft: byte array)
        (commitments: ISuppressionCommitments)
        (instant: DateTimeOffset)
        =
        task {
            commitments.Admit()
            let draftCommitment = commitments.ApprovalDraft canonicalDraft

            if draftCommitment.Length <> 32 || draftHash <> SHA256.HashData(canonicalDraft) then
                corrupt ()

            let! selected =
                CaseLifecycleRead.approvals
                    connection
                    transaction
                    projection.CaseId
                    change
                    draftHash
                    instant

            let receipts = ResizeArray<ErasurePurgeApprovalReceipt>()

            for approval in selected do
                let! value =
                    row
                        connection
                        transaction
                        witness
                        projection
                        change
                        draftHash
                        draftCommitment
                        commitments
                        approval

                receipts.Add(value)

            return selected, receipts |> Seq.toList
        }
