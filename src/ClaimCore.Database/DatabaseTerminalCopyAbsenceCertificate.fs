namespace ClaimCore.Database

open System
open System.Security.Cryptography
open ClaimCore.Postgres
open ClaimCore.Witness

[<NoEquality; NoComparison>]
type internal TerminalCopyAbsenceRequest =
    {
        CaseId: Guid
        PruneEventId: Guid
        CutoffSequence: int64
        CutoffHash: byte array
        PolicyId: string
        SuppressionUntil: DateTimeOffset
        WriterGeneration: int64
        ObservedAt: DateTimeOffset
    }

/// Build the opaque terminal certificate only after signed and witnessed evidence is checked.
module internal DatabaseTerminalCopyAbsenceCertificate =
    let bind
        caseId
        pruneEventId
        cutoffSequence
        cutoffHash
        policyId
        suppressionUntil
        writerGeneration
        observedAt
        =
        {
            CaseId = caseId
            PruneEventId = pruneEventId
            CutoffSequence = cutoffSequence
            CutoffHash = cutoffHash
            PolicyId = policyId
            SuppressionUntil = suppressionUntil
            WriterGeneration = writerGeneration
            ObservedAt = observedAt
        }
        : TerminalCopyAbsenceRequest

    let private facts
        (witness: WitnessProtocol)
        (request: TerminalCopyAbsenceRequest)
        (evidence: SignedCopyLocationEvidence)
        copyCount
        =
        ({
            InstallationId = witness.Identity.InstallationId
            LineageId = witness.Identity.LineageId
            Epoch = witness.Identity.Epoch
            CaseId = request.CaseId
            PruneEventId = request.PruneEventId
            CutoffSequence = request.CutoffSequence
            CutoffHash = Convert.ToHexStringLower request.CutoffHash
            InventoryDigest = Convert.ToHexStringLower evidence.RegistrySha256
            CopyCount = copyCount
            WriterGeneration = request.WriterGeneration
            PolicyId = request.PolicyId
            SuppressionUntil = request.SuppressionUntil
            VerifiedAt = request.ObservedAt
            ValidUntil = min evidence.RegistryExpiresAt evidence.InspectionExpiresAt
        }
        : TerminalCopyAbsenceFacts)

    let private mint (value: TerminalCopyAbsenceFacts) proof (tip: Snapshot) =
        OwnerCopyAbsenceCertificate.FromVerifiedOwnerEvidence(
            value.InstallationId,
            value.LineageId,
            value.Epoch,
            value.CaseId,
            value.PruneEventId,
            value.CutoffSequence,
            value.CutoffHash,
            value.InventoryDigest,
            value.CopyCount,
            value.WriterGeneration,
            value.PolicyId,
            value.SuppressionUntil,
            value.VerifiedAt,
            value.ValidUntil,
            proof,
            tip.TipSequence,
            tip.TipHash
        )

    let certify
        (witness: WitnessProtocol)
        (request: TerminalCopyAbsenceRequest)
        (evidence: SignedCopyLocationEvidence)
        registryHolder
        verifierHolder
        copyCount
        deletionDigest
        (tip: Snapshot)
        =
        let value = facts witness request evidence copyCount

        let canonical =
            DatabaseTerminalCopyAbsenceProof.encode
                value
                evidence
                registryHolder
                verifierHolder
                deletionDigest
                tip.TipSequence
                tip.TipHash

        try
            mint value (SHA256.HashData(canonical)) tip
        finally
            CryptographicOperations.ZeroMemory(canonical)
