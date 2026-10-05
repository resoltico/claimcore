namespace ClaimCore.Postgres

open System.Threading
open System.Security.Cryptography
open ClaimCore.Witness
open DataAuditCommon

/// Proves the independent terminal W0/W1 projection and ciphertext against exact
/// signed candidate bytes, without treating missing primary rows as recovered.
module internal DataAuditInstallationLossWitness =
    let private identityMatches
        (tip: Snapshot)
        (row: LossRetirementEvidence)
        (value: InstallationLossRetirementDecision)
        id
        =
        row.RetirementId = id
        && row.InstallationId = tip.Identity.InstallationId
        && row.LineageId = tip.Identity.LineageId
        && row.Epoch = tip.Identity.Epoch
        && row.InstallationId = value.InstallationId
        && row.LineageId = value.LineageId
        && row.Epoch = value.Epoch
        && row.PreviousSequence = value.PreviousSequence
        && row.PreviousHash = value.PreviousHash

    let private decisionMatches
        (row: LossRetirementEvidence)
        (value: InstallationLossRetirementDecision)
        =
        row.CanonicalSha256 = SHA256.HashData(row.Canonical)
        && row.SignerOneId = value.SignerOneId
        && row.SignerTwoId = value.SignerTwoId
        && row.OwnerOneActorId = value.OwnerOneActorId
        && row.OwnerTwoActorId = value.OwnerTwoActorId
        && row.OperationSetKind =
            InstallationLossRetirementCandidate.operationSetName value.OperationSet
        && row.KnownOperationCount = value.KnownOperationCount
        && row.KnownOperationDigest = value.KnownOperationDigest
        && row.IntentSequence = value.PreviousSequence + 1L

    let read (witness: WitnessProtocol) (tip: Snapshot) id (ct: CancellationToken) =
        task {
            let! retained = witness.EvidenceStore.TryReadLossRetirement(id, ct)
            let row = retained |> Option.defaultWith corrupt

            let value =
                InstallationLossRetirementCandidate.parse row.Canonical
                |> Option.defaultWith corrupt

            if not (identityMatches tip row value id && decisionMatches row value) then
                corrupt ()

            return row, value
        }

    let private decrypt (witness: WitnessProtocol) (ticket: Ticket) encrypted expected =
        let name = ClaimCore.Witness.Encoding.phase ticket.Phase

        let plain =
            witness.KeyCustody.Decrypt(
                ticket.KeyId,
                witness.AssociatedData(ticket.OperationId, name),
                encrypted
            )

        try
            if plain <> expected then
                corrupt ()
        finally
            CryptographicOperations.ZeroMemory(plain)
            CryptographicOperations.ZeroMemory(expected)

    let intent
        (witness: WitnessProtocol)
        (row: LossRetirementEvidence)
        (ticket: Ticket)
        (ct: CancellationToken)
        =
        task {
            if
                ticket.Phase <> Intent
                || ticket.ScopeKind <> Installation
                || ticket.SubjectCaseId.IsSome
                || ticket.Sequence <> row.IntentSequence
                || ticket.EntryHash <> row.IntentHash
            then
                corrupt ()

            let expected =
                InstallationLossRetirementWitness.candidateDigest
                    row.Canonical
                    row.SignatureOne
                    row.SignatureTwo
                    row.KnownOperationDigest

            let! observed = witness.EvidenceStore.TryReadEvidence(row.RetirementId, Intent, ct)
            let evidence = observed |> Option.defaultWith corrupt

            if evidence.Ticket <> ticket then
                corrupt ()

            decrypt witness ticket evidence.EncryptedPayload expected
        }

    let settlement
        (witness: WitnessProtocol)
        (tip: Snapshot)
        (row: LossRetirementEvidence)
        (ticket: Ticket)
        (ct: CancellationToken)
        =
        task {
            if
                row.SettlementSequence <> Some ticket.Sequence
                || row.SettlementHash <> Some ticket.EntryHash
                || ticket.Sequence <> row.IntentSequence + 1L
                || not tip.LossRetired
                || tip.LossRetirementPending
                || tip.LossRetirementSequence <> Some ticket.Sequence
                || tip.LossRetirementHash <> Some ticket.EntryHash
            then
                corrupt ()

            let! observedIntent =
                witness.EvidenceStore.TryReadEvidence(row.RetirementId, Intent, ct)

            let intent = observedIntent |> Option.defaultWith corrupt

            let expected =
                InstallationLossRetirementWitness.settlementDigest row.Canonical intent.Ticket

            let! observed =
                witness.EvidenceStore.TryReadEvidence(row.RetirementId, SettledAuthority, ct)

            let evidence = observed |> Option.defaultWith corrupt

            if evidence.Ticket <> ticket then
                corrupt ()

            decrypt witness ticket evidence.EncryptedPayload expected
        }
