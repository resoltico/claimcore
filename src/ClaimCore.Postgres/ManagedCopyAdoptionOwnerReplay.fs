namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness
open WitnessProtocolReconciliation

/// A prior primary commit with missing witness settlement is knowledge-uncertain, never a
/// failed adoption. Only exact same signed bytes and event ID can settle its old intent.
module internal ManagedCopyAdoptionOwnerReplay =
    let private same (submission: CopyAdoptionSubmission) (stored: StoredCopyAdoptionEvent) =
        stored.EventId = submission.AdoptionEventId
        && stored.ApprovalId = submission.ApprovalId
        && stored.CustodianCanonical = submission.Custodian.Canonical
        && stored.CustodianSignature = submission.Custodian.Signature
        && stored.RegistryCanonical = submission.Registry.Canonical
        && stored.RegistrySignature = submission.Registry.Signature
        && stored.InspectionCanonical = submission.Inspection.Canonical
        && stored.InspectionSignature = submission.Inspection.Signature
        && stored.CandidateHash = SHA256.HashData(stored.Canonical)

    let replay
        ownerConnection
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments)
        (submission: CopyAdoptionSubmission)
        (stored: StoredCopyAdoptionEvent)
        (ct: CancellationToken)
        =
        task {
            if not (same submission stored) then
                return CopyAdoptionOwnerOutcome.ResourceUnavailable
            else
                try
                    do!
                        witness.ReconcileAuthority(
                            submission.AdoptionEventId,
                            stored.WitnessSequence,
                            stored.WitnessEpoch,
                            stored.WitnessHash,
                            stored.Canonical,
                            ct
                        )

                    do!
                        witness.VerifyAuthorityEvidenceForCase(
                            submission.AdoptionEventId,
                            stored.WitnessSequence,
                            stored.WitnessEpoch,
                            stored.WitnessHash,
                            stored.CandidateHash,
                            stored.CaseId,
                            CancellationToken.None
                        )

                    use connection = new NpgsqlConnection(ownerConnection)
                    do! connection.OpenAsync(ct)
                    let! _ = DataAudit.runWithSuppression connection witness (Some commitments) ct

                    let revision =
                        ManagedCopyAdoptionCustody.parse stored.CustodianCanonical
                        |> Option.map _.Revision
                        |> Option.defaultValue 0L

                    if revision < 1L then
                        return CopyAdoptionOwnerOutcome.Unconfirmed submission.AdoptionEventId
                    else
                        return
                            CopyAdoptionOwnerOutcome.Adopted(submission.AdoptionEventId, revision)
                with _ ->
                    return CopyAdoptionOwnerOutcome.Unconfirmed submission.AdoptionEventId
        }
