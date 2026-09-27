namespace ClaimCore.Domain

open System

/// Private proof tokens are minted by the trusted Application authority only after the storage
/// layer has replayed exact witnessed inventory and independently signed absence observations.
type CopyAbsenceSeal =
    private
        {
            InstallationId: Guid
            LineageId: Guid
            WitnessEpoch: int64
            CaseId: Guid
            PruneEventId: Guid
            CutoffSequence: int64
            CutoffHash: string
            InventoryDigest: string
            RelevantCopyCount: int64
            WriterGeneration: int64
            PolicyId: string
            SuppressionUntil: DateTimeOffset
            VerifiedAt: DateTimeOffset
            ValidUntil: DateTimeOffset
        }

/// The recovery fence must be externally anchored, purpose-signed and verified by the owner
/// workflow. Domain checks identity and timing, not signatures or remote hardware custody.
type RecoveryFenceSeal =
    private
        {
            InstallationId: Guid
            LineageId: Guid
            WitnessEpoch: int64
            CaseId: Guid
            OldWriterGeneration: int64
            NewWriterGeneration: int64
            CopyInventoryDigest: string
            FenceDigest: string
            AuthorityRevision: int64
            AuthorityHash: string
            WitnessSettlementSequence: int64
            WitnessSettlementHash: string
            ArtifactCutoffSequence: int64
            PolicyId: string
            SuppressionUntil: DateTimeOffset
            VerifiedAt: DateTimeOffset
            ValidUntil: DateTimeOffset
        }

module internal OwnerErasureAuthority =
    let private hash value =
        not (String.IsNullOrEmpty value)
        && value.Length = 64
        && (value |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))

    let private utc (value: DateTimeOffset) = value.Offset = TimeSpan.Zero

    let private policy value =
        not (String.IsNullOrWhiteSpace value)
        && value.Length <= 128
        && value = value.Trim()
        && (value |> Seq.forall (Char.IsControl >> not))

    let copyAbsence (facts: CopyAbsenceFacts) : CopyAbsenceSeal option =
        if
            facts.InstallationId = Guid.Empty
            || facts.LineageId = Guid.Empty
            || facts.CaseId = Guid.Empty
            || facts.PruneEventId = Guid.Empty
            || facts.WitnessEpoch < 1L
            || facts.CutoffSequence < 1L
            || facts.RelevantCopyCount < 0L
            || facts.WriterGeneration < 1L
            || not (hash facts.CutoffHash && hash facts.InventoryDigest)
            || not (policy facts.PolicyId)
            || not (utc facts.SuppressionUntil && utc facts.VerifiedAt && utc facts.ValidUntil)
            || facts.ValidUntil <= facts.VerifiedAt
        then
            None
        else
            Some
                {
                    InstallationId = facts.InstallationId
                    LineageId = facts.LineageId
                    WitnessEpoch = facts.WitnessEpoch
                    CaseId = facts.CaseId
                    PruneEventId = facts.PruneEventId
                    CutoffSequence = facts.CutoffSequence
                    CutoffHash = facts.CutoffHash
                    InventoryDigest = facts.InventoryDigest
                    RelevantCopyCount = facts.RelevantCopyCount
                    WriterGeneration = facts.WriterGeneration
                    PolicyId = facts.PolicyId
                    SuppressionUntil = facts.SuppressionUntil
                    VerifiedAt = facts.VerifiedAt
                    ValidUntil = facts.ValidUntil
                }

    let private recoveryIdentity (facts: RecoveryFenceFacts) =
        facts.InstallationId <> Guid.Empty
        && facts.LineageId <> Guid.Empty
        && facts.CaseId <> Guid.Empty
        && facts.WitnessEpoch > 0L
        && facts.OldWriterGeneration > 0L
        && facts.OldWriterGeneration < Int64.MaxValue
        && facts.NewWriterGeneration = facts.OldWriterGeneration + 1L

    let private recoveryBounds (facts: RecoveryFenceFacts) =
        facts.AuthorityRevision >= 0L
        && facts.ArtifactCutoffSequence > 0L
        && facts.WitnessSettlementSequence > facts.ArtifactCutoffSequence
        && hash facts.CopyInventoryDigest
        && hash facts.FenceDigest
        && hash facts.AuthorityHash
        && hash facts.WitnessSettlementHash

    let recoveryFence (facts: RecoveryFenceFacts) : RecoveryFenceSeal option =
        if
            not (recoveryIdentity facts && recoveryBounds facts)
            || not (policy facts.PolicyId)
            || not (utc facts.SuppressionUntil && utc facts.VerifiedAt && utc facts.ValidUntil)
            || facts.ValidUntil <= facts.VerifiedAt
        then
            None
        else
            Some
                {
                    InstallationId = facts.InstallationId
                    LineageId = facts.LineageId
                    WitnessEpoch = facts.WitnessEpoch
                    CaseId = facts.CaseId
                    OldWriterGeneration = facts.OldWriterGeneration
                    NewWriterGeneration = facts.NewWriterGeneration
                    CopyInventoryDigest = facts.CopyInventoryDigest
                    FenceDigest = facts.FenceDigest
                    AuthorityRevision = facts.AuthorityRevision
                    AuthorityHash = facts.AuthorityHash
                    WitnessSettlementSequence = facts.WitnessSettlementSequence
                    WitnessSettlementHash = facts.WitnessSettlementHash
                    ArtifactCutoffSequence = facts.ArtifactCutoffSequence
                    PolicyId = facts.PolicyId
                    SuppressionUntil = facts.SuppressionUntil
                    VerifiedAt = facts.VerifiedAt
                    ValidUntil = facts.ValidUntil
                }

    let approval
        action
        eventId
        caseId
        authorityRevision
        authorityHash
        eventDigest
        policyId
        suppressionUntil
        copyDigest
        relevantCopyCount
        writerGeneration
        fenceDigest
        approvalId
        approverId
        grantRevision
        witnessSequence
        witnessHash
        expiresAt
        =
        {
            ApprovalId = approvalId
            Action = action
            EventId = eventId
            CaseId = caseId
            ExpectedAuthorityRevision = authorityRevision
            ExpectedAuthorityHash = authorityHash
            EventDigest = eventDigest
            PolicyId = policyId
            SuppressionUntil = suppressionUntil
            CopyInventoryDigest = copyDigest
            RelevantCopyCount = relevantCopyCount
            ExpectedWriterGeneration = writerGeneration
            RecoveryFenceDigest = fenceDigest
            ApproverId = approverId
            GrantRevision = grantRevision
            WitnessSequence = witnessSequence
            WitnessHash = witnessHash
            ExpiresAt = expiresAt
        }

    let private bound (decision: OwnerErasureDecision) (value: OwnerErasureApproval) =
        value.Action = decision.Action
        && value.EventId = decision.EventId
        && value.CaseId = decision.CaseId
        && value.ExpectedAuthorityRevision = decision.ExpectedAuthorityRevision
        && value.ExpectedAuthorityHash = decision.ExpectedAuthorityHash
        && value.EventDigest = decision.EventDigest
        && value.PolicyId = decision.PolicyId
        && value.SuppressionUntil = decision.SuppressionUntil
        && value.CopyInventoryDigest = decision.CopyInventoryDigest
        && value.RelevantCopyCount = decision.RelevantCopyCount
        && value.ExpectedWriterGeneration = decision.ExpectedWriterGeneration
        && value.RecoveryFenceDigest = decision.RecoveryFenceDigest
        && value.ApprovalId <> Guid.Empty
        && value.ApproverId <> Guid.Empty
        && value.GrantRevision > 0L
        && value.WitnessSequence > 0L
        && hash value.WitnessHash
        && utc value.ExpiresAt
        && value.ExpiresAt > decision.At
        && value.ExpiresAt <= decision.ValidUntil

    let private decisionEvidence (decision: OwnerErasureDecision) =
        decision.InstallationId <> Guid.Empty
        && decision.LineageId <> Guid.Empty
        && decision.WitnessEpoch > 0L
        && decision.PruneEventId <> Guid.Empty
        && decision.WitnessCutoffSequence > 0L
        && decision.ExpectedAuthorityRevision >= 0L
        && decision.RelevantCopyCount >= 0L
        && decision.ExpectedWriterGeneration > 0L
        && hash decision.ExpectedAuthorityHash
        && hash decision.WitnessCutoffHash
        && hash decision.CopyInventoryDigest
        && hash decision.EventDigest

    let private decisionPolicy (decision: OwnerErasureDecision) =
        policy decision.PolicyId
        && utc decision.At
        && utc decision.ValidUntil
        && utc decision.SuppressionUntil
        && decision.ValidUntil > decision.At
        && decision.ValidUntil - decision.At <= TimeSpan.FromHours(24.0)

    let private checkApprovalPair (decision: OwnerErasureDecision) =
        match decision.Approvals with
        | [ first; second ] when
            first.ApprovalId <> second.ApprovalId && first.ApproverId <> second.ApproverId
            ->
            if bound decision first && bound decision second then
                Ok()
            else
                Error LifecycleRefusal.ApprovalMismatch
        | _ -> Error LifecycleRefusal.ApprovalRequired

    let checkDecision action caseId revision (decision: OwnerErasureDecision) =
        if
            decision.Action <> action
            || decision.EventId = Guid.Empty
            || decision.CaseId = Guid.Empty
        then
            Error LifecycleRefusal.InvalidIdentity
        elif decision.CaseId <> caseId then
            Error LifecycleRefusal.WrongCase
        elif decision.ExpectedRevision <> revision then
            Error LifecycleRefusal.VersionConflict
        elif not (decisionEvidence decision && decisionPolicy decision) then
            Error LifecycleRefusal.ErasureEvidenceIncomplete
        else
            checkApprovalPair decision

    let copyMatches (decision: OwnerErasureDecision) (seal: CopyAbsenceSeal) =
        seal.InstallationId = decision.InstallationId
        && seal.LineageId = decision.LineageId
        && seal.WitnessEpoch = decision.WitnessEpoch
        && seal.CaseId = decision.CaseId
        && seal.PruneEventId = decision.PruneEventId
        && seal.CutoffSequence = decision.WitnessCutoffSequence
        && seal.CutoffHash = decision.WitnessCutoffHash
        && seal.InventoryDigest = decision.CopyInventoryDigest
        && seal.RelevantCopyCount = decision.RelevantCopyCount
        && seal.WriterGeneration = decision.ExpectedWriterGeneration
        && seal.PolicyId = decision.PolicyId
        && seal.SuppressionUntil = decision.SuppressionUntil
        && seal.VerifiedAt <= decision.At
        && decision.At < seal.ValidUntil

    let fenceMatches
        (decision: OwnerErasureDecision)
        (copy: CopyAbsenceSeal)
        (seal: RecoveryFenceSeal)
        =
        seal.InstallationId = copy.InstallationId
        && seal.LineageId = copy.LineageId
        && seal.WitnessEpoch = copy.WitnessEpoch
        && seal.CaseId = copy.CaseId
        && seal.CopyInventoryDigest = copy.InventoryDigest
        && seal.NewWriterGeneration = decision.ExpectedWriterGeneration
        && seal.NewWriterGeneration = copy.WriterGeneration
        && Some seal.FenceDigest = decision.RecoveryFenceDigest
        && seal.AuthorityRevision = decision.ExpectedAuthorityRevision
        && seal.AuthorityHash = decision.ExpectedAuthorityHash
        && seal.ArtifactCutoffSequence >= copy.CutoffSequence
        && seal.PolicyId = decision.PolicyId
        && seal.SuppressionUntil = decision.SuppressionUntil
        && seal.VerifiedAt <= decision.At
        && decision.At < seal.ValidUntil
