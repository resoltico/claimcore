namespace ClaimCore.Postgres

open System
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open WitnessProtocolReconciliation

module internal CaseLifecycleApplyDecision =
    let private verifyApproval
        connection
        transaction
        (witness: WitnessProtocol)
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        (draftHash: byte array)
        (approval: LifecycleApprovalEvidence)
        =
        task {
            let! row = CaseLifecycleRead.approvalById connection transaction approval.ApprovalId

            match row with
            | None -> raise WitnessPending
            | Some evidence ->
                let expected =
                    CaseLifecycleCandidate.approval
                        approval.ApprovalId
                        change.EventId
                        projection.CaseId
                        draftHash
                        approval.ApproverId
                        evidence.GrantRevision
                        evidence.ApprovedAt
                        approval.ExpiresAt

                try
                    if evidence.DraftHash <> draftHash || expected <> evidence.Canonical then
                        raise WitnessPending
                finally
                    CaseLifecycleStoreSupport.clear expected

                witness.ReconcileAuthority(
                    approval.ApprovalId,
                    evidence.WitnessSequence,
                    evidence.WitnessEpoch,
                    evidence.WitnessHash,
                    evidence.Canonical
                )
        }

    let private historicalPayment
        connection
        transaction
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        =
        match change.Action with
        | LifecycleMutation.VoidDataEntryError _ ->
            CaseLifecyclePaymentEvidence.historicalPayment connection transaction projection.CaseId
        | _ -> System.Threading.Tasks.Task.FromResult false

    let private witnessedRequest
        connection
        transaction
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments)
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        =
        match change.Action with
        | LifecycleMutation.MarkErasurePending _ ->
            CaseErasureRequestProof.read
                connection
                transaction
                witness
                commitments
                projection.CaseId
                change.CaseReference
                System.Threading.CancellationToken.None
        | _ -> System.Threading.Tasks.Task.FromResult None

    let private approvals
        connection
        transaction
        (witness: WitnessProtocol)
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        (draftHash: byte array)
        (instant: DateTimeOffset)
        =
        task {
            let! selected =
                CaseLifecycleRead.approvals
                    connection
                    transaction
                    projection.CaseId
                    change
                    draftHash
                    instant

            for approval in selected do
                do!
                    verifyApproval
                        connection
                        transaction
                        witness
                        projection
                        change
                        draftHash
                        approval

            return selected
        }

    let decide
        connection
        transaction
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments)
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        actorId
        (draftHash: byte array)
        instant
        =
        task {
            let! payment = historicalPayment connection transaction projection change

            let! witnessed =
                witnessedRequest connection transaction witness commitments projection change

            let! selected =
                approvals connection transaction witness projection change draftHash instant

            let result =
                CaseLifecycleDecisions.decide
                    projection.CaseId
                    (Claim.view projection.Claim)
                    projection.State
                    change
                    actorId
                    (Convert.ToHexStringLower draftHash)
                    payment
                    witnessed
                    selected
                    instant

            return result, selected
        }
