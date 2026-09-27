module ClaimCore.Tests.CaseDispositionTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures
open ClaimCore.Tests.CaseLifecycleTestSupport

let private voidCase =
    testCase
        "[CC-LIFE-001] void advances revision without changing business fields and fences ordinary access"
        (fun () ->
            let snapshot = opened () |> Claim.view
            let before = state snapshot
            let after = voidUnpaid snapshot
            Expect.equal after.Snapshot.Fields snapshot.Fields "All thirteen fields unchanged"
            Expect.equal after.Snapshot.Version (snapshot.Version + 1L) "New revision"

            Expect.equal
                (CaseLifecycle.disposition after.State)
                CaseDisposition.VoidedDataEntryError
                "Voided"

            Expect.equal
                (CaseLifecycle.privacy after.State)
                PrivacyPhase.Active
                "Void is not erasure"

            for access in
                [
                    LifecycleAccess.OrdinaryRead
                    LifecycleAccess.OrdinaryCommand
                    LifecycleAccess.RecoveryResolve
                    LifecycleAccess.Export
                    LifecycleAccess.DefaultList
                ] do
                Expect.isFalse (CaseLifecycle.permits access after.State) "Ordinary access fenced"

            Expect.isTrue
                (CaseLifecycle.permits LifecycleAccess.CustodianAudit after.State)
                "Auditor can inspect"

            Expect.isTrue
                (CaseLifecycle.permits LifecycleAccess.OrdinaryRead before)
                "Original state unchanged")

let private rejectUnapprovedPaidVoid snapshot =
    Expect.equal
        (CaseLifecycle.voidDataEntryError
            (state snapshot)
            snapshot
            LifecycleEvidenceAuthority.historicalPaymentAssertion
            (decision LifecycleAction.VoidDataEntryError snapshot.Version []))
        (Error LifecycleRefusal.ApprovalRequired)
        "Historical payment requires dual approval"

let private paidCase =
    testCase
        "[CC-LIFE-001] payment assertion requires two bound approvals and is not reversed"
        (fun () ->
            let snapshot = paid () |> Claim.view
            let action = LifecycleAction.VoidDataEntryError
            rejectUnapprovedPaidVoid snapshot
            rejectUnapprovedPaidVoid (opened () |> Claim.view)

            let wrong =
                { approved action snapshot.Version with
                    Approvals =
                        [
                            LifecycleEvidenceAuthority.approval
                                action
                                (operationId action)
                                caseId
                                snapshot.Version
                                (String.replicate 64 "b")
                                firstApprover
                                (instant.AddHours 1.0)
                            approval action snapshot.Version secondApprover
                        ]
                }

            Expect.equal
                (CaseLifecycle.voidDataEntryError
                    (state snapshot)
                    snapshot
                    LifecycleEvidenceAuthority.historicalPaymentAssertion
                    wrong)
                (Error LifecycleRefusal.ApprovalMismatch)
                "Digest-bound approvals"

            let after =
                CaseLifecycle.voidDataEntryError
                    (state snapshot)
                    snapshot
                    LifecycleEvidenceAuthority.historicalPaymentAssertion
                    (approved action snapshot.Version)
                |> accepted

            Expect.equal
                after.Snapshot.Fields.PaymentDate
                snapshot.Fields.PaymentDate
                "No payment reversal"

            Expect.equal
                after.Snapshot.Fields.PayableAmount
                snapshot.Fields.PayableAmount
                "Decision remains")

let private approvalCase =
    testCase "[CC-LIFE-001] duplicate, self, expired and stale approvals fail closed" (fun () ->
        let voided = opened () |> Claim.view |> voidUnpaid
        let action = LifecycleAction.ReinstateVoided
        let revision = voided.Snapshot.Version

        let same, self, expired = invalidApprovalDecisions action revision

        for invalid in [ same; self ] do
            Expect.equal
                (CaseLifecycle.reinstateVoided voided.State voided.Snapshot invalid)
                (Error LifecycleRefusal.ApprovalRequired)
                "Distinct non-self approvers"

        Expect.equal
            (CaseLifecycle.reinstateVoided voided.State voided.Snapshot expired)
            (Error LifecycleRefusal.ApprovalExpired)
            "Expiry bound"

        Expect.equal
            (CaseLifecycle.reinstateVoided
                voided.State
                voided.Snapshot
                (wrongOperationApproval action revision))
            (Error LifecycleRefusal.ApprovalMismatch)
            "Approval is operation-bound"

        Expect.equal
            (CaseLifecycle.reinstateVoided
                voided.State
                voided.Snapshot
                (approved action (revision - 1L)))
            (Error LifecycleRefusal.VersionConflict)
            "Revision bound")

let private reinstatementCase =
    testCase "[CC-LIFE-001] explicit reinstatement is a new revision; erasure dominates" (fun () ->
        let snapshot = opened () |> Claim.view
        let voided = voidUnpaid snapshot
        let action = LifecycleAction.ReinstateVoided

        let reinstated =
            CaseLifecycle.reinstateVoided
                voided.State
                voided.Snapshot
                (approved action voided.Snapshot.Version)
            |> accepted

        Expect.equal reinstated.Snapshot.Fields snapshot.Fields "Facts unchanged"
        Expect.equal reinstated.Snapshot.Version (snapshot.Version + 2L) "Separate revision"

        Expect.equal
            (CaseLifecycle.disposition reinstated.State)
            CaseDisposition.Active
            "Explicit reinstatement"

        let erasure =
            CaseLifecycle.requestErasure voided.State actorId instant "Privacy request"
            |> accepted

        Expect.equal
            (CaseLifecycle.reinstateVoided
                erasure
                voided.Snapshot
                (approved action voided.Snapshot.Version))
            (Error LifecycleRefusal.ErasureHasBegun)
            "Erasure dominates")

let private malformedCase =
    testCase "[CC-LIFE-001] malformed reason, time and identity are refused" (fun () ->
        let snapshot = opened () |> Claim.view
        let basic = decision LifecycleAction.VoidDataEntryError snapshot.Version []

        let apply submitted =
            CaseLifecycle.voidDataEntryError
                (state snapshot)
                snapshot
                LifecycleEvidenceAuthority.noHistoricalPaymentAssertionVerified
                submitted

        Expect.equal
            (apply { basic with Reason = " " })
            (Error LifecycleRefusal.InvalidReason)
            "Reason required"

        Expect.equal
            (apply { basic with OperationId = Guid.Empty })
            (Error LifecycleRefusal.InvalidIdentity)
            "Operation ID required"

        Expect.equal
            (apply
                { basic with
                    Reason = "unsafe\u202edirection"
                })
            (Error LifecycleRefusal.InvalidReason)
            "Bidi format control refused"

        Expect.equal
            (apply
                { basic with
                    Reason = String.replicate 501 "x"
                })
            (Error LifecycleRefusal.InvalidReason)
            "Reason bounded"

        Expect.equal
            (apply
                { basic with
                    At = instant.ToOffset(TimeSpan.FromHours 1.0)
                })
            (Error LifecycleRefusal.InvalidTime)
            "UTC required"

        Expect.equal
            (apply { basic with CaseId = Guid.NewGuid() })
            (Error LifecycleRefusal.WrongCase)
            "Case bound")

let tests =
    testList
        "Case disposition"
        [ voidCase; paidCase; approvalCase; reinstatementCase; malformedCase ]
