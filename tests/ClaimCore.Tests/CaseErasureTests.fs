module ClaimCore.Tests.CaseErasureTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures
open ClaimCore.Tests.CaseLifecycleTestSupport
open ClaimCore.Tests.OwnerErasureTestSupport

let private tombstone () =
    let snapshot = opened () |> Claim.view
    let pending = requestedPending snapshot

    CaseLifecycle.restoreTombstone
        caseId
        snapshot.Version
        (CaseLifecycle.disposition pending)
        PrivacyPhase.ErasurePending
        (Array.create 32 0x42uy)
    |> accepted

let private holdCase =
    testCase "[CC-LIFE-001] holds remain orthogonal and block owner certification" (fun () ->
        let snapshot = opened () |> Claim.view
        let pending = requestedPending snapshot

        let hold =
            {
                Id = Guid.NewGuid()
                Ground = "Synthetic preservation ground"
                ReviewOn = DateOnly(2026, 10, 1)
                RecordedBy = actorId
                RecordedAt = instant
            }

        let held = CaseLifecycle.recordHold pending hold |> accepted

        let request =
            ownerDecision
                OwnerErasureAction.ConfirmManagedPayloadAbsence
                instant
                snapshot.Version
                None

        Expect.equal
            (CaseLifecycleOwnerErasure.confirmManagedPayloadAbsence held request (copySeal instant))
            (Error LifecycleRefusal.HoldActive)
            "No proof bypasses an active hold"

        let released =
            CaseLifecycle.releaseHold held hold.Id secondApprover instant "Ground reviewed"
            |> accepted

        Expect.isEmpty (CaseLifecycle.holds released) "Explicit release is required"

        Expect.isFalse
            (CaseLifecycle.permits LifecycleAccess.OrdinaryRead released)
            "Access remains fenced")

let private pendingWithoutExactProof =
    testCase
        "[CC-LIFE-001] structural copy evidence and distinct stewards gate later phase"
        (fun () ->
            let state = tombstone ()

            let request =
                ownerDecision
                    OwnerErasureAction.ConfirmManagedPayloadAbsence
                    instant
                    (CaseLifecycle.revision state)
                    None

            let duplicate =
                { request with
                    Approvals = [ request.Approvals.Head; request.Approvals.Head ]
                }

            Expect.equal
                (CaseLifecycleOwnerErasure.confirmManagedPayloadAbsence
                    state
                    duplicate
                    (copySeal instant))
                (Error LifecycleRefusal.ApprovalRequired)
                "One steward cannot fill both approval slots"

            let stale = copySeal (instant.AddDays(-2.0))

            Expect.equal
                (CaseLifecycleOwnerErasure.confirmManagedPayloadAbsence state request stale)
                (Error LifecycleRefusal.ErasureEvidenceIncomplete)
                "An expired copy-absence seal does not advance privacy"

            let suppressed =
                CaseLifecycleOwnerErasure.confirmManagedPayloadAbsence
                    state
                    request
                    (copySeal instant)
                |> accepted

            Expect.equal
                (CaseLifecycle.privacy suppressed)
                PrivacyPhase.PayloadErasedSuppressionRetained
                "Only the copy-verified pseudonymous state advances"

            Expect.isFalse
                (CaseLifecycle.permits LifecycleAccess.CustodianAudit suppressed)
                "Erased claimant payload is not readable")

let private finalRequiresPolicyAndFence =
    testCase "[CC-LIFE-001] final requires reviewed horizon and exact recovery fence" (fun () ->
        let pending = tombstone ()

        let copied =
            CaseLifecycleOwnerErasure.confirmManagedPayloadAbsence
                pending
                (ownerDecision
                    OwnerErasureAction.ConfirmManagedPayloadAbsence
                    instant
                    (CaseLifecycle.revision pending)
                    None)
                (copySeal instant)
            |> accepted

        let early =
            ownerDecision
                OwnerErasureAction.CompleteSuppressionHorizon
                instant
                (CaseLifecycle.revision copied)
                (Some fenceDigest)

        Expect.equal
            (CaseLifecycleOwnerErasure.completeSuppressionHorizon
                copied
                early
                (copySeal instant)
                (recoveryFence instant))
            (Error LifecycleRefusal.ErasureEvidenceIncomplete)
            "A signed fence alone cannot skip the policy horizon"

        let later = suppressionUntil.AddDays(1.0)

        let finalDecision =
            ownerDecision
                OwnerErasureAction.CompleteSuppressionHorizon
                later
                (CaseLifecycle.revision copied)
                (Some fenceDigest)

        let final =
            CaseLifecycleOwnerErasure.completeSuppressionHorizon
                copied
                finalDecision
                (copySeal later)
                (recoveryFence later)
            |> accepted

        Expect.equal
            (CaseLifecycle.privacy final)
            PrivacyPhase.ErasureFinal
            "Policy horizon completed"

        Expect.isFalse
            (CaseLifecycle.permits LifecycleAccess.OrdinaryCommand final)
            "No authority revives")

let private proofBindings =
    testCase "[CC-LIFE-001] copy count and writer generation bind the terminal proof" (fun () ->
        let pending = tombstone ()

        let confirm =
            ownerDecision
                OwnerErasureAction.ConfirmManagedPayloadAbsence
                instant
                (CaseLifecycle.revision pending)
                None

        for wrong in [ copySealWith 1L 1L instant; copySealWith 0L 2L instant ] do
            Expect.equal
                (CaseLifecycleOwnerErasure.confirmManagedPayloadAbsence pending confirm wrong)
                (Error LifecycleRefusal.ErasureEvidenceIncomplete)
                "Count or writer generation cannot diverge from reviewed proposal"

        let copied =
            CaseLifecycleOwnerErasure.confirmManagedPayloadAbsence
                pending
                confirm
                (copySeal instant)
            |> accepted

        let later = suppressionUntil.AddDays(1.0)

        let finish =
            ownerDecision
                OwnerErasureAction.CompleteSuppressionHorizon
                later
                (CaseLifecycle.revision copied)
                (Some fenceDigest)

        Expect.equal
            (CaseLifecycleOwnerErasure.completeSuppressionHorizon
                copied
                finish
                (copySeal later)
                (recoveryFenceWith 2L 3L later))
            (Error LifecycleRefusal.ErasureEvidenceIncomplete)
            "Different handoff generation cannot certify the case"

        Expect.isNone
            (tryRecoveryFenceWith Int64.MaxValue Int64.MinValue later)
            "Overflowed generation successor is never a valid fence")

let tests =
    testList
        "Case erasure"
        [
            holdCase
            pendingWithoutExactProof
            finalRequiresPolicyAndFence
            proofBindings
        ]
