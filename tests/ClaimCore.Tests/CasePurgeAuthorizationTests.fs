module ClaimCore.Tests.CasePurgeAuthorizationTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures
open ClaimCore.Tests.CaseLifecycleTestSupport

let private purgeHasNoPrematureCompletion =
    testCase "[CC-ERASE-001] owner live purge authorization keeps privacy pending" (fun _ ->
        let snapshot = opened () |> Claim.view
        let pending = requestedPending snapshot
        let action = LifecycleAction.PurgeLivePayload

        Expect.equal
            (CaseLifecycle.authorizeLivePurge pending (decision action snapshot.Version []))
            (Error LifecycleRefusal.ApprovalRequired)
            "An owner cannot self-authorize an irreversible purge"

        Expect.equal
            (CaseLifecycle.authorizeLivePurge pending (approved action snapshot.Version))
            (Ok())
            "Two independent approvals authorize only the SQL purge"

        Expect.equal
            (CaseLifecycle.privacy pending)
            PrivacyPhase.ErasurePending
            "Authorization never claims payload absence")

let private technicalExecutorHasNoInventedActor =
    testCase
        "[CC-ERASE-001] owner purge uses technical execution and two distinct stewards"
        (fun _ ->
            let snapshot = opened () |> Claim.view
            let pending = requestedPending snapshot
            let action = LifecycleAction.PurgeLivePayload

            let ownerDecision: OwnerPurgeDecision =
                {
                    OperationId = operationId action
                    CaseId = caseId
                    ExpectedRevision = snapshot.Version
                    EventDigest = digest
                    At = instant
                    Reason = "Synthetic live purge"
                    Approvals =
                        [
                            approval action snapshot.Version firstApprover
                            approval action snapshot.Version secondApprover
                        ]
                }

            Expect.equal
                (CaseLifecycle.authorizeOwnerLivePurge pending ownerDecision)
                (Ok())
                "Technical owner execution needs exact two human approvals, not a fake actor ID"

            let duplicate =
                { ownerDecision with
                    Approvals = [ ownerDecision.Approvals.Head; ownerDecision.Approvals.Head ]
                }

            Expect.equal
                (CaseLifecycle.authorizeOwnerLivePurge pending duplicate)
                (Error LifecycleRefusal.ApprovalRequired)
                "One steward cannot occupy both approvals")

let private heldOrAlreadyPurged =
    testCase "[CC-ERASE-001] hold and tombstone prevent a second live purge" (fun _ ->
        let snapshot = opened () |> Claim.view
        let pending = requestedPending snapshot

        let hold =
            {
                Id = Guid.NewGuid()
                Ground = "Synthetic legal hold"
                ReviewOn = DateOnly(2026, 9, 20)
                RecordedBy = actorId
                RecordedAt = instant
            }

        let held = CaseLifecycle.recordHold pending hold |> accepted
        let purge = approved LifecycleAction.PurgeLivePayload snapshot.Version

        Expect.equal
            (CaseLifecycle.authorizeLivePurge held purge)
            (Error LifecycleRefusal.HoldActive)
            "A hold cannot be bypassed"

        let commitment = Array.create 32 0x42uy

        let tombstone =
            CaseLifecycle.restoreTombstone
                caseId
                snapshot.Version
                CaseDisposition.Active
                PrivacyPhase.ErasurePending
                commitment
            |> accepted

        Expect.equal
            (CaseLifecycle.authorizeLivePurge tombstone purge)
            (Error LifecycleRefusal.WrongPrivacyPhase)
            "A payload-free tombstone cannot be purged again"

        Expect.isFalse
            (CaseLifecycle.permits LifecycleAccess.OrdinaryRead tombstone)
            "Tombstone grants no ordinary access")

let tests =
    testList
        "owner live purge authorization"
        [
            purgeHasNoPrematureCompletion
            technicalExecutorHasNoInventedActor
            heldOrAlreadyPurged
        ]
