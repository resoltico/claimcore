namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql

/// Exact independent absence and one-use human approval under the adopted-copy lock.
module internal ManagedCopyAdoptedDeletionEvidence =
    let private absenceMatches
        (value: AdoptedCopyTransition)
        (origin: VerifiedCopyAdoptionOrigin)
        (absence: VerifiedManagedCopyDeletionAbsence)
        =
        absence.CopyId = value.CopyId
        && absence.WitnessCutoffSequence = value.ActionWitnessCutoffSequence
        && absence.WitnessCutoffHash = value.ActionWitnessCutoffHash
        && value.DeletionProofSha256 = Some absence.InspectionReportSha256
        && value.LastVerifiedAt = Some absence.ObservedAt
        && absence.VerifierSigningKeyId <> origin.CustodianSigningKeyId
        && absence.RegistryHolderActorId <> absence.VerifierHolderActorId
        && origin.CustodianHolderActorId <> absence.VerifierHolderActorId

    let private approval
        connection
        transaction
        (witness: WitnessProtocol)
        (value: AdoptedCopyTransition)
        (origin: VerifiedCopyAdoptionOrigin)
        (state: AdoptedCopyCurrent)
        now
        proof
        =
        task {
            let binding: ManagedCopyDeletionEvidenceRead.TransitionBinding =
                {
                    EventId = value.EventId
                    CopyId = value.CopyId
                    SourceCaseId = Some value.SourceCaseId
                    ApprovalId = value.DeletionApprovalId
                    WitnessCutoffSequence = value.ActionWitnessCutoffSequence
                    WitnessCutoffHash = value.ActionWitnessCutoffHash
                }

            let! expiry =
                ManagedCopyDeletionEvidenceRead.verifyBinding
                    connection
                    transaction
                    witness
                    binding
                    proof
                    state.Revision
                    origin.LocationCommitment
                    origin.CustodianHolderActorId
                    now
                    CancellationToken.None

            match expiry with
            | None -> return false
            | Some approvalExpiry ->
                let! fresh = Sql.databaseNow connection transaction CancellationToken.None

                return
                    fresh < approvalExpiry
                    && fresh < proof.RegistryExpiresAt
                    && fresh < proof.InspectionExpiresAt
        }

    let verify
        connection
        transaction
        (witness: WitnessProtocol)
        (verifier: ICopyAbsenceVerifier)
        (value: AdoptedCopyTransition)
        (origin: VerifiedCopyAdoptionOrigin)
        (state: AdoptedCopyCurrent)
        now
        =
        task {
            let! absence =
                verifier.Verify(
                    connection,
                    transaction,
                    witness,
                    value.CopyId,
                    value.ActionWitnessCutoffSequence,
                    value.ActionWitnessCutoffHash,
                    now,
                    CancellationToken.None
                )

            match absence with
            | Some proof when absenceMatches value origin proof ->
                return! approval connection transaction witness value origin state now proof
            | _ -> return false
        }
