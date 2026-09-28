namespace ClaimCore.Postgres

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open OwnerTerminalDecisionData

[<RequireQualifiedAccess>]
type internal OwnerTerminalOutcome =
    | Advanced of eventId: Guid * phase: PrivacyPhase
    | Refused of LifecycleRefusal
    | ResourceUnavailable
    | InventoryUnknown
    | RecoveryFenceUnknown
    | AuditUnavailable of safeStage: string
    | Unconfirmed of eventId: Guid

/// Only an owner issuer that has independently replayed the signed closed location registry,
/// current copy-ID set, witnessed deletion approval/use and fresh absence observations may mint
/// this certificate. Its digest is audit evidence, never an authorization by itself.
[<Sealed>]
type internal OwnerCopyAbsenceCertificate
    private
    (
        facts: OwnerTerminalEvidenceData.CopyAbsenceFacts,
        proofSha256: byte array,
        tipSequence: int64,
        tipHash: byte array
    ) =
    let proof = Array.copy proofSha256
    let hash = Array.copy tipHash

    member _.Facts = facts
    member _.CaseId = facts.CaseId
    member _.InventoryDigest = facts.InventoryDigest
    member _.WriterGeneration = facts.WriterGeneration
    member _.PolicyId = facts.PolicyId
    member _.SuppressionUntil = facts.SuppressionUntil
    member _.ValidUntil = facts.ValidUntil
    member _.SignedProofSha256 = Array.copy proof
    member _.WitnessTipSequence = tipSequence
    member _.WitnessTipHash = Array.copy hash

    static member internal FromVerifiedIssuer
        (facts, proofSha256: byte array, tipSequence, tipHash: byte array)
        =
        if proofSha256.Length <> 32 || tipSequence < 1L || tipHash.Length <> 32 then
            invalidArg (nameof proofSha256) "Copy absence issuer proof is incomplete."

        OwnerCopyAbsenceCertificate(facts, proofSha256, tipSequence, tipHash)

    static member internal FromVerifiedOwnerEvidence
        (
            installationId,
            lineageId,
            epoch,
            caseId,
            pruneEventId,
            cutoffSequence,
            cutoffHash,
            inventoryDigest,
            copyCount,
            writerGeneration,
            policyId,
            suppressionUntil,
            verifiedAt,
            validUntil,
            proofSha256,
            tipSequence,
            tipHash
        ) =
        let facts: OwnerTerminalEvidenceData.CopyAbsenceFacts =
            {
                InstallationId = installationId
                LineageId = lineageId
                WitnessEpoch = epoch
                CaseId = caseId
                PruneEventId = pruneEventId
                CutoffSequence = cutoffSequence
                CutoffHash = cutoffHash
                InventoryDigest = inventoryDigest
                RelevantCopyCount = copyCount
                WriterGeneration = writerGeneration
                PolicyId = policyId
                SuppressionUntil = suppressionUntil
                VerifiedAt = verifiedAt
                ValidUntil = validUntil
            }

        OwnerCopyAbsenceCertificate.FromVerifiedIssuer(facts, proofSha256, tipSequence, tipHash)

/// The separate checkpoint/recovery-fence issuer must verify purpose-distinct signing custody,
/// immutable external publication, restored-pair replay and old-host/session/credential isolation.
[<Sealed>]
type internal OwnerRecoveryFenceCertificate
    private (facts: OwnerTerminalEvidenceData.RecoveryFenceFacts, proofSha256: byte array) =
    let proof = Array.copy proofSha256
    member _.Facts = facts
    member _.SignedProofSha256 = Array.copy proof

    static member internal FromVerifiedIssuer(facts, proofSha256: byte array) =
        if proofSha256.Length <> 32 then
            invalidArg (nameof proofSha256) "Recovery fence issuer proof is incomplete."

        OwnerRecoveryFenceCertificate(facts, proofSha256)

    static member internal FromVerifiedOwnerEvidence
        (
            installationId,
            lineageId,
            epoch,
            caseId,
            oldGeneration,
            newGeneration,
            copyDigest,
            fenceDigest,
            authorityRevision,
            authorityHash,
            settlementSequence,
            settlementHash,
            artifactCutoff,
            policyId,
            suppressionUntil,
            verifiedAt,
            validUntil,
            proofSha256
        ) =
        let facts: OwnerTerminalEvidenceData.RecoveryFenceFacts =
            {
                InstallationId = installationId
                LineageId = lineageId
                WitnessEpoch = epoch
                CaseId = caseId
                OldWriterGeneration = oldGeneration
                NewWriterGeneration = newGeneration
                CopyInventoryDigest = copyDigest
                FenceDigest = fenceDigest
                AuthorityRevision = authorityRevision
                AuthorityHash = authorityHash
                WitnessSettlementSequence = settlementSequence
                WitnessSettlementHash = settlementHash
                ArtifactCutoffSequence = artifactCutoff
                PolicyId = policyId
                SuppressionUntil = suppressionUntil
                VerifiedAt = verifiedAt
                ValidUntil = validUntil
            }

        OwnerRecoveryFenceCertificate.FromVerifiedIssuer(facts, proofSha256)

[<NoEquality; NoComparison>]
type internal OwnerTerminalReady =
    {
        Copy: OwnerCopyAbsenceCertificate
        Fence: OwnerRecoveryFenceCertificate option
        Approvals: ApprovalEvidence list
        Stored: StoredTerminalTombstone
        ObservedAt: DateTimeOffset
        EventDigest: string
        NextPhase: PrivacyPhase
    }

type internal ICopyErasureCertification =
    abstract RequireAllAbsent:
        primary: NpgsqlConnection *
        transaction: NpgsqlTransaction *
        witness: WitnessProtocol *
        caseId: Guid *
        pruneEventId: Guid *
        cutoffSequence: int64 *
        cutoffHash: byte array *
        policyId: string *
        suppressionUntil: DateTimeOffset *
        expectedWriterGeneration: int64 *
        observedAt: DateTimeOffset *
        cancellationToken: CancellationToken ->
            Task<OwnerCopyAbsenceCertificate option>

type internal IRecoveryFenceCertification =
    abstract RequireVerifiedFence:
        primary: NpgsqlConnection *
        transaction: NpgsqlTransaction *
        witness: WitnessProtocol *
        caseId: Guid *
        copy: OwnerCopyAbsenceCertificate *
        proposal: TerminalFinalProposal *
        observedAt: DateTimeOffset *
        cancellationToken: CancellationToken ->
            Task<OwnerRecoveryFenceCertificate option>
