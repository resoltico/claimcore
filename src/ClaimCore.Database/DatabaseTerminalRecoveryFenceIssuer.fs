namespace ClaimCore.Database

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres

/// A historical, independently signed W2 does not itself finish erasure. The issuer also
/// requires the fresh all-copy absence certificate and proves every known recovery export has
/// expired, lost its retained payload and predates W2 settlement.
[<Sealed>]
type internal DatabaseTerminalRecoveryFenceIssuer
    private (candidate: WriterActivationEvidence option) =
    let mutable disposed = false

    let facts
        (witness: WitnessProtocol)
        (copy: OwnerCopyAbsenceCertificate)
        (proposal: TerminalFinalProposal)
        (value: WriterActivationEvidence)
        (row: TerminalRecoveryFenceRow)
        observedAt
        : TerminalRecoveryFenceFacts =
        {
            InstallationId = witness.Identity.InstallationId
            LineageId = witness.Identity.LineageId
            WitnessEpoch = witness.Identity.Epoch
            CaseId = proposal.Copy.CaseId
            OldWriterGeneration = proposal.OldWriterGeneration
            NewWriterGeneration = proposal.NewWriterGeneration
            CopyInventoryDigest = copy.InventoryDigest
            FenceDigest = Convert.ToHexStringLower value.ProbeEvidenceSha256
            AuthorityRevision = row.AuthorityRevision
            AuthorityHash = Convert.ToHexStringLower row.AuthorityHash
            WitnessSettlementSequence = row.SettlementSequence
            WitnessSettlementHash = Convert.ToHexStringLower row.SettlementHash
            ArtifactCutoffSequence = row.ArtifactCutoffSequence
            PolicyId = proposal.Copy.PolicyId
            SuppressionUntil = proposal.Copy.SuppressionUntil
            VerifiedAt = observedAt
            ValidUntil = copy.ValidUntil
        }

    let matching
        (witness: WitnessProtocol)
        (copy: OwnerCopyAbsenceCertificate)
        (proposal: TerminalFinalProposal)
        (value: WriterActivationEvidence)
        observedAt
        =
        let expected = proposal.Copy

        value.InstallationId = witness.Identity.InstallationId
        && value.LineageId = witness.Identity.LineageId
        && value.Epoch = witness.Identity.Epoch
        && value.WriterGeneration = proposal.NewWriterGeneration
        && proposal.OldWriterGeneration + 1L = proposal.NewWriterGeneration
        && copy.CaseId = expected.CaseId
        && copy.InventoryDigest = expected.CopyInventoryDigest
        && copy.WriterGeneration = expected.ExpectedWriterGeneration
        && copy.WriterGeneration = proposal.NewWriterGeneration
        && copy.PolicyId = expected.PolicyId
        && copy.SuppressionUntil = expected.SuppressionUntil
        && copy.ValidUntil > observedAt
        && observedAt >= expected.SuppressionUntil
        && Convert.ToHexStringLower value.ProbeEvidenceSha256 = proposal.RecoveryFenceDigest

    let certificate (value: TerminalRecoveryFenceFacts) digest =
        OwnerRecoveryFenceCertificate.FromVerifiedOwnerEvidence(
            value.InstallationId,
            value.LineageId,
            value.WitnessEpoch,
            value.CaseId,
            value.OldWriterGeneration,
            value.NewWriterGeneration,
            value.CopyInventoryDigest,
            value.FenceDigest,
            value.AuthorityRevision,
            value.AuthorityHash,
            value.WitnessSettlementSequence,
            value.WitnessSettlementHash,
            value.ArtifactCutoffSequence,
            value.PolicyId,
            value.SuppressionUntil,
            value.VerifiedAt,
            value.ValidUntil,
            digest
        )

    let certify
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        caseId
        (copy: OwnerCopyAbsenceCertificate)
        (proposal: TerminalFinalProposal)
        observedAt
        (ct: CancellationToken)
        =
        task {
            ct.ThrowIfCancellationRequested()

            match candidate with
            | _ when disposed -> return None
            | None -> return None
            | Some value when
                not (matching witness copy proposal value observedAt)
                || caseId <> proposal.Copy.CaseId
                ->
                return None
            | Some value ->
                OwnerConnection.requireIdentity connection

                let row =
                    DatabaseTerminalRecoveryFenceRows.verify
                        connection
                        transaction
                        witness
                        value
                        proposal
                        observedAt

                let proofFacts = facts witness copy proposal value row observedAt
                let canonical = DatabaseTerminalRecoveryFenceProof.encode proofFacts value copy row

                try
                    let digest = SHA256.HashData(canonical)

                    return Some(certificate proofFacts digest)
                finally
                    CryptographicOperations.ZeroMemory(canonical)
        }

    interface IRecoveryFenceCertification with
        member _.RequireVerifiedFence
            (primary, transaction, witness, caseId, copy, proposal, observedAt, ct)
            =
            task {
                try
                    return! certify primary transaction witness caseId copy proposal observedAt ct
                with _ ->
                    return None
            }

    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed <- true

                candidate
                |> Option.iter (fun value ->
                    for bytes in
                        [
                            value.SignedReport
                            value.ReportSignature
                            value.SignedFence
                            value.FenceSignature
                            value.SignedSupplement
                            value.SupplementSignature
                        ] do
                        CryptographicOperations.ZeroMemory(bytes))

    static member Load(ownerConnection: string, proposal: TombstoneTerminalProposal) =
        let verified =
            match proposal with
            | TombstoneTerminalProposal.ConfirmManagedPayloadAbsence _ -> None
            | TombstoneTerminalProposal.CompleteSuppressionHorizon _ ->
                DatabaseTerminalRecoveryFenceInputs.verifiedCandidate ownerConnection

        new DatabaseTerminalRecoveryFenceIssuer(verified)
