namespace ClaimCore.Domain

/// Technical owner transitions only. A public case-work request cannot obtain the sealed
/// copy-absence or recovery-fence proofs needed to call these decisions.
module CaseLifecycleOwnerErasure =
    let confirmManagedPayloadAbsence state decision copySeal =
        let caseId, revision, privacy, holdsEmpty, payloadSuppressed =
            CaseLifecycle.ownerErasureContext state

        if privacy <> PrivacyPhase.ErasurePending then
            Error LifecycleRefusal.WrongPrivacyPhase
        elif not holdsEmpty then
            Error LifecycleRefusal.HoldActive
        elif not payloadSuppressed then
            Error LifecycleRefusal.ErasureEvidenceIncomplete
        else
            match
                OwnerErasureAuthority.checkDecision
                    OwnerErasureAction.ConfirmManagedPayloadAbsence
                    caseId
                    revision
                    decision
            with
            | Error refusal -> Error refusal
            | Ok() when
                decision.RecoveryFenceDigest.IsNone
                && OwnerErasureAuthority.copyMatches decision copySeal
                ->
                CaseLifecycle.ownerErasureTransition
                    state
                    PrivacyPhase.PayloadErasedSuppressionRetained
                |> Ok
            | Ok() -> Error LifecycleRefusal.ErasureEvidenceIncomplete

    let completeSuppressionHorizon state decision copySeal recoveryFence =
        let caseId, revision, privacy, holdsEmpty, payloadSuppressed =
            CaseLifecycle.ownerErasureContext state

        if privacy <> PrivacyPhase.PayloadErasedSuppressionRetained then
            Error LifecycleRefusal.WrongPrivacyPhase
        elif not holdsEmpty then
            Error LifecycleRefusal.HoldActive
        elif not payloadSuppressed then
            Error LifecycleRefusal.ErasureEvidenceIncomplete
        else
            match
                OwnerErasureAuthority.checkDecision
                    OwnerErasureAction.CompleteSuppressionHorizon
                    caseId
                    revision
                    decision
            with
            | Error refusal -> Error refusal
            | Ok() when
                decision.At >= decision.SuppressionUntil
                && OwnerErasureAuthority.copyMatches decision copySeal
                && OwnerErasureAuthority.fenceMatches decision copySeal recoveryFence
                ->
                CaseLifecycle.ownerErasureTransition state PrivacyPhase.ErasureFinal |> Ok
            | Ok() -> Error LifecycleRefusal.ErasureEvidenceIncomplete
