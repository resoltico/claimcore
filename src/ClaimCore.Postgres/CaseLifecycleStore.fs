namespace ClaimCore.Postgres

open Npgsql
open ClaimCore.Application

/// The actor-bound lifecycle store is the only runtime writer of disposition, holds and
/// erasure fences. All paths use the shared authority lock and independent witness.
type internal PostgresCaseLifecycleStore
    (dataSource: NpgsqlDataSource, witness: WitnessProtocol, commitments: ISuppressionCommitments) =
    interface ICaseLifecycleStore with
        member _.Review(actor, caseReference) =
            CaseLifecycleReview.review dataSource witness actor caseReference

        member _.Apply(actor, change, instant) =
            CaseLifecycleApply.apply dataSource witness commitments actor change instant

        member _.Approve(actor, change, approvalId, expiresAt, instant) =
            CaseLifecycleApprove.approve
                dataSource
                witness
                actor
                change
                approvalId
                expiresAt
                instant
