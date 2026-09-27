module ClaimCore.Tests.CaseLifecycleTestSupport

open System
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures

let caseId = Guid.Parse "10000000-0000-4000-8000-000000000001"
let actorId = Guid.Parse "10000000-0000-4000-8000-000000000002"
let firstApprover = Guid.Parse "10000000-0000-4000-8000-000000000003"
let secondApprover = Guid.Parse "10000000-0000-4000-8000-000000000004"
let instant = DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero)
let digest = String.replicate 64 "a"

let operationId =
    function
    | LifecycleAction.VoidDataEntryError -> Guid.Parse "20000000-0000-4000-8000-000000000001"
    | LifecycleAction.ReinstateVoided -> Guid.Parse "20000000-0000-4000-8000-000000000002"
    | LifecycleAction.PurgeLivePayload -> Guid.Parse "20000000-0000-4000-8000-000000000005"

let state snapshot =
    CaseLifecycle.initial caseId snapshot |> accepted

let decision action revision approvals =
    {
        Action = action
        OperationId = operationId action
        CaseId = caseId
        ExpectedRevision = revision
        EventDigest = digest
        ActorId = actorId
        At = instant
        Reason = "Synthetic case correction"
        Approvals = approvals
    }

let approval action revision approver =
    LifecycleEvidenceAuthority.approval
        action
        (operationId action)
        caseId
        revision
        digest
        approver
        (instant.AddHours 1.0)

let approved action revision =
    decision
        action
        revision
        [
            approval action revision firstApprover
            approval action revision secondApprover
        ]

let wrongOperationApproval action revision =
    { approved action revision with
        Approvals =
            [
                LifecycleEvidenceAuthority.approval
                    action
                    (Guid.NewGuid())
                    caseId
                    revision
                    digest
                    firstApprover
                    (instant.AddHours 1.0)
                approval action revision secondApprover
            ]
    }

let invalidApprovalDecisions action revision =
    let same =
        decision
            action
            revision
            [
                approval action revision firstApprover
                approval action revision firstApprover
            ]

    let self =
        decision
            action
            revision
            [ approval action revision actorId; approval action revision secondApprover ]

    let expired =
        { approved action revision with
            Approvals =
                [
                    LifecycleEvidenceAuthority.approval
                        action
                        (operationId action)
                        caseId
                        revision
                        digest
                        firstApprover
                        instant
                    approval action revision secondApprover
                ]
        }

    same, self, expired

let requestedPending snapshot =
    state snapshot
    |> fun current ->
        CaseLifecycle.requestErasure current actorId instant "Synthetic privacy request"
        |> accepted
    |> fun requested ->
        let fence =
            LifecycleEvidenceAuthority.witnessedFence caseId (Guid.NewGuid()) digest
            |> Option.get

        CaseLifecycle.markErasurePending requested fence actorId instant "Synthetic confirmed fence"
        |> accepted

let voidUnpaid snapshot =
    CaseLifecycle.voidDataEntryError
        (state snapshot)
        snapshot
        LifecycleEvidenceAuthority.noHistoricalPaymentAssertionVerified
        (decision LifecycleAction.VoidDataEntryError snapshot.Version [])
    |> accepted
