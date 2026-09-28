namespace ClaimCore.Application

open System
open ClaimCore.Domain
open OwnerTerminalEvidenceData
open OwnerTerminalDecisionData

module internal OwnerTerminalDecisionKernel =
    let private action =
        function
        | TombstoneTerminalProposal.ConfirmManagedPayloadAbsence _ ->
            OwnerErasureAction.ConfirmManagedPayloadAbsence
        | TombstoneTerminalProposal.CompleteSuppressionHorizon _ ->
            OwnerErasureAction.CompleteSuppressionHorizon

    let private fenceDigest =
        function
        | TombstoneTerminalProposal.ConfirmManagedPayloadAbsence _ -> None
        | TombstoneTerminalProposal.CompleteSuppressionHorizon value ->
            Some value.RecoveryFenceDigest

    let private decision
        proposal
        (projection: Projection)
        eventDigest
        (observedAt: DateTimeOffset)
        (approvals: ApprovalEvidence list)
        =
        let common = TombstoneTerminalProposal.copy proposal
        let ownerAction = action proposal
        let recoveryDigest = fenceDigest proposal

        let approval value =
            OwnerErasureAuthority.approval
                ownerAction
                common.EventId
                common.CaseId
                common.ExpectedAuthorityRevision
                common.ExpectedAuthorityHash
                eventDigest
                common.PolicyId
                common.SuppressionUntil
                common.CopyInventoryDigest
                common.RelevantCopyCount
                common.ExpectedWriterGeneration
                recoveryDigest
                value.ApprovalId
                value.ActorId
                value.GrantRevision
                value.WitnessSequence
                value.WitnessHash
                value.ExpiresAt

        {
            Action = ownerAction
            EventId = common.EventId
            CaseId = common.CaseId
            ExpectedRevision = projection.SourceRevision
            ExpectedAuthorityRevision = common.ExpectedAuthorityRevision
            ExpectedAuthorityHash = common.ExpectedAuthorityHash
            InstallationId = common.InstallationId
            LineageId = common.LineageId
            WitnessEpoch = common.WitnessEpoch
            PruneEventId = common.PruneEventId
            WitnessCutoffSequence = common.WitnessCutoffSequence
            WitnessCutoffHash = common.WitnessCutoffHash
            CopyInventoryDigest = common.CopyInventoryDigest
            RelevantCopyCount = common.RelevantCopyCount
            ExpectedWriterGeneration = common.ExpectedWriterGeneration
            RecoveryFenceDigest = recoveryDigest
            PolicyId = common.PolicyId
            SuppressionUntil = common.SuppressionUntil
            EventDigest = eventDigest
            At = observedAt
            ValidUntil = common.ValidUntil
            Approvals = approvals |> List.map approval
        }

    let evaluate
        proposal
        projection
        eventDigest
        observedAt
        approvals
        (copy: CopyAbsenceFacts)
        (fence: RecoveryFenceFacts option)
        =
        let common = TombstoneTerminalProposal.copy proposal

        match
            CaseLifecycle.restoreTombstone
                common.CaseId
                projection.SourceRevision
                projection.Disposition
                projection.Privacy
                projection.ReferenceCommitment
        with
        | Error refusal -> Error refusal
        | Ok state ->
            match OwnerTerminalEvidenceAuthority.mintCopy copy with
            | None -> Error LifecycleRefusal.ErasureEvidenceIncomplete
            | Some copySeal ->
                let ownerDecision = decision proposal projection eventDigest observedAt approvals

                match proposal, fence with
                | TombstoneTerminalProposal.ConfirmManagedPayloadAbsence _, None ->
                    CaseLifecycleOwnerErasure.confirmManagedPayloadAbsence
                        state
                        ownerDecision
                        copySeal
                    |> Result.map CaseLifecycle.privacy
                | TombstoneTerminalProposal.CompleteSuppressionHorizon _, Some facts ->
                    match OwnerTerminalEvidenceAuthority.mintFence facts with
                    | None -> Error LifecycleRefusal.ErasureEvidenceIncomplete
                    | Some fenceSeal ->
                        CaseLifecycleOwnerErasure.completeSuppressionHorizon
                            state
                            ownerDecision
                            copySeal
                            fenceSeal
                        |> Result.map CaseLifecycle.privacy
                | _ -> Error LifecycleRefusal.ErasureEvidenceIncomplete
