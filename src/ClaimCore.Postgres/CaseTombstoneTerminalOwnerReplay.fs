namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness
open WitnessProtocolReconciliation

/// Exact committed-event recovery never creates another owner effect or rebases its proposal.
module internal CaseTombstoneTerminalOwnerReplay =
    let private matches proposal (stored: StoredTerminalEvent) =
        let expected = CaseTombstoneTerminalCandidate.proposal proposal

        try
            let decoded = CaseTombstoneTerminalEventCodec.decode stored.Canonical

            stored.EventId = TombstoneTerminalProposal.eventId proposal
            && stored.CaseId = TombstoneTerminalProposal.caseId proposal
            && stored.CandidateHash = SHA256.HashData(stored.Canonical)
            && (decoded
                |> Option.exists (fun value ->
                    let embedded = CaseTombstoneTerminalCandidate.proposal value.Proposal

                    try
                        embedded = expected
                    finally
                        CryptographicOperations.ZeroMemory(embedded)))
        finally
            CryptographicOperations.ZeroMemory(expected)

    let private reconcile (witness: WitnessProtocol) (stored: StoredTerminalEvent) ct =
        task {
            let! observed = witness.EvidenceStore.TryReadEvidence(stored.EventId, Intent, ct)
            let intent = observed |> Option.defaultWith (fun () -> raise WitnessPending)

            if
                intent.Ticket.ScopeKind <> Case
                || intent.Ticket.SubjectCaseId <> Some stored.CaseId
            then
                raise WitnessPending

            do!
                witness.ReconcileAuthority(
                    stored.EventId,
                    stored.WitnessSequence,
                    stored.WitnessEpoch,
                    stored.WitnessHash,
                    stored.Canonical,
                    ct
                )

            do!
                witness.VerifyAuthorityEvidenceForCase(
                    stored.EventId,
                    stored.WitnessSequence,
                    stored.WitnessEpoch,
                    stored.WitnessHash,
                    stored.CandidateHash,
                    stored.CaseId,
                    CancellationToken.None
                )
        }

    let private audit ownerConnection witness commitments ct =
        task {
            use connection = new NpgsqlConnection(ownerConnection)
            do! connection.OpenAsync(ct)
            let! _ = DataAudit.runWithSuppression connection witness (Some commitments) ct
            return ()
        }

    let run ownerConnection witness commitments proposal (stored: StoredTerminalEvent) ct =
        task {
            if not (matches proposal stored) then
                return
                    OwnerTerminalOutcome.Refused ClaimCore.Domain.LifecycleRefusal.ApprovalMismatch
            else
                try
                    do! reconcile witness stored ct
                    do! audit ownerConnection witness commitments ct

                    let phase =
                        CaseLifecycleCandidate.parsePrivacy stored.ResultingPhase
                        |> Option.defaultWith (fun () -> raise WitnessPending)

                    return OwnerTerminalOutcome.Advanced(stored.EventId, phase)
                with _ ->
                    return OwnerTerminalOutcome.Unconfirmed stored.EventId
        }
