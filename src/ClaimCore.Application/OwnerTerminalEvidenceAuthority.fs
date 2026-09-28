namespace ClaimCore.Application

open System
open ClaimCore.Domain
open OwnerTerminalEvidenceData

module internal OwnerTerminalEvidenceAuthority =
    let mintCopy (value: CopyAbsenceFacts) =
        OwnerErasureAuthority.copyAbsence
            {
                InstallationId = value.InstallationId
                LineageId = value.LineageId
                WitnessEpoch = value.WitnessEpoch
                CaseId = value.CaseId
                PruneEventId = value.PruneEventId
                CutoffSequence = value.CutoffSequence
                CutoffHash = value.CutoffHash
                InventoryDigest = value.InventoryDigest
                RelevantCopyCount = value.RelevantCopyCount
                WriterGeneration = value.WriterGeneration
                PolicyId = value.PolicyId
                SuppressionUntil = value.SuppressionUntil
                VerifiedAt = value.VerifiedAt
                ValidUntil = value.ValidUntil
            }

    let mintFence (value: RecoveryFenceFacts) =
        OwnerErasureAuthority.recoveryFence
            {
                InstallationId = value.InstallationId
                LineageId = value.LineageId
                WitnessEpoch = value.WitnessEpoch
                CaseId = value.CaseId
                OldWriterGeneration = value.OldWriterGeneration
                NewWriterGeneration = value.NewWriterGeneration
                CopyInventoryDigest = value.CopyInventoryDigest
                FenceDigest = value.FenceDigest
                AuthorityRevision = value.AuthorityRevision
                AuthorityHash = value.AuthorityHash
                WitnessSettlementSequence = value.WitnessSettlementSequence
                WitnessSettlementHash = value.WitnessSettlementHash
                ArtifactCutoffSequence = value.ArtifactCutoffSequence
                PolicyId = value.PolicyId
                SuppressionUntil = value.SuppressionUntil
                VerifiedAt = value.VerifiedAt
                ValidUntil = value.ValidUntil
            }
