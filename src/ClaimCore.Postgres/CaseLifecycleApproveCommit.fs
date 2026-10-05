namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open WitnessProtocolReconciliation

module internal CaseLifecycleApproveCommit =
    let private persistAndSettle
        connection
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        approvalId
        expiresAt
        instant
        draftHash
        canonical
        (intent: WitnessIntent)
        =
        task {
            do!
                CaseLifecycleApprovalWrite.persistApproval
                    connection
                    transaction
                    projection
                    change
                    approvalId
                    expiresAt
                    instant
                    context
                    draftHash
                    canonical
                    intent

            do! transaction.CommitAsync()

            do!
                witness.ReconcileAuthority(
                    approvalId,
                    intent.Ticket.Sequence,
                    intent.Ticket.Epoch,
                    intent.Ticket.EntryHash,
                    canonical,
                    CancellationToken.None
                )

            return
                LifecycleWriteOutcome.Applied(
                    approvalId,
                    (Claim.view projection.Claim).Version,
                    projection.Sequence
                )
        }

    let emit
        connection
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        approvalId
        expiresAt
        instant
        draftHash
        ct
        =
        task {
            let canonical =
                CaseLifecycleCandidate.approval
                    approvalId
                    change.EventId
                    projection.CaseId
                    draftHash
                    context.Binding.ActorId
                    context.Binding.GrantRevision
                    instant
                    expiresAt

            try
                let! intent =
                    witness.BeginAuthority(approvalId, canonical, Some projection.CaseId, ct)

                try
                    return!
                        persistAndSettle
                            connection
                            transaction
                            witness
                            context
                            projection
                            change
                            approvalId
                            expiresAt
                            instant
                            draftHash
                            canonical
                            intent
                with _ ->
                    return LifecycleWriteOutcome.Unconfirmed approvalId
            finally
                CaseLifecycleStoreSupport.clear canonical
        }
