module ClaimCore.Tests.OwnerErasureTestSupport

open System
open ClaimCore.Domain
open ClaimCore.Tests.CaseLifecycleTestSupport

let installationId = Guid.Parse "30000000-0000-4000-8000-000000000001"
let lineageId = Guid.Parse "30000000-0000-4000-8000-000000000002"
let pruneEventId = Guid.Parse "30000000-0000-4000-8000-000000000003"
let copyDigest = String.replicate 64 "b"
let fenceDigest = String.replicate 64 "c"
let authorityHash = String.replicate 64 "d"
let policyId = "synthetic-retention-policy"
let suppressionUntil = instant.AddDays(30.0)

let copySealWith relevantCount writerGeneration (at: DateTimeOffset) =
    OwnerErasureAuthority.copyAbsence
        {
            InstallationId = installationId
            LineageId = lineageId
            WitnessEpoch = 1L
            CaseId = caseId
            PruneEventId = pruneEventId
            CutoffSequence = 5L
            CutoffHash = digest
            InventoryDigest = copyDigest
            RelevantCopyCount = relevantCount
            WriterGeneration = writerGeneration
            PolicyId = policyId
            SuppressionUntil = suppressionUntil
            VerifiedAt = at.AddMinutes(-1.0)
            ValidUntil = at.AddHours(1.0)
        }
    |> Option.get

let copySeal at =
    copySealWith 0L (if at >= suppressionUntil then 2L else 1L) at

let tryRecoveryFenceWith oldGeneration newGeneration (at: DateTimeOffset) =
    OwnerErasureAuthority.recoveryFence
        {
            InstallationId = installationId
            LineageId = lineageId
            WitnessEpoch = 1L
            CaseId = caseId
            OldWriterGeneration = oldGeneration
            NewWriterGeneration = newGeneration
            CopyInventoryDigest = copyDigest
            FenceDigest = fenceDigest
            AuthorityRevision = 0L
            AuthorityHash = authorityHash
            WitnessSettlementSequence = 8L
            WitnessSettlementHash = digest
            ArtifactCutoffSequence = 6L
            PolicyId = policyId
            SuppressionUntil = suppressionUntil
            VerifiedAt = at.AddMinutes(-1.0)
            ValidUntil = at.AddHours(1.0)
        }

let recoveryFenceWith oldGeneration newGeneration at =
    tryRecoveryFenceWith oldGeneration newGeneration at |> Option.get

let recoveryFence at = recoveryFenceWith 1L 2L at

let ownerDecision action (at: DateTimeOffset) revision recoveryDigest =
    let eventId = Guid.NewGuid()

    let approval approver sequence =
        OwnerErasureAuthority.approval
            action
            eventId
            caseId
            0L
            authorityHash
            digest
            policyId
            suppressionUntil
            copyDigest
            0L
            (if at >= suppressionUntil then 2L else 1L)
            recoveryDigest
            (Guid.NewGuid())
            approver
            1L
            sequence
            digest
            (at.AddMinutes(30.0))

    {
        Action = action
        EventId = eventId
        CaseId = caseId
        ExpectedRevision = revision
        ExpectedAuthorityRevision = 0L
        ExpectedAuthorityHash = authorityHash
        InstallationId = installationId
        LineageId = lineageId
        WitnessEpoch = 1L
        PruneEventId = pruneEventId
        WitnessCutoffSequence = 5L
        WitnessCutoffHash = digest
        CopyInventoryDigest = copyDigest
        RelevantCopyCount = 0L
        ExpectedWriterGeneration = if at >= suppressionUntil then 2L else 1L
        RecoveryFenceDigest = recoveryDigest
        PolicyId = policyId
        SuppressionUntil = suppressionUntil
        EventDigest = digest
        At = at
        ValidUntil = at.AddHours(1.0)
        Approvals = [ approval firstApprover 10L; approval secondApprover 12L ]
    }
