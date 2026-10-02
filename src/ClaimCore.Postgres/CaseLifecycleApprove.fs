namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain

module internal CaseLifecycleApprove =
    let private approvalTime (change: LifecycleChange) approvalAt expiresAt =
        CaseLifecycleStoreSupport.validInstant approvalAt
        && CaseLifecycleStoreSupport.validInstant expiresAt
        && expiresAt > approvalAt
        && expiresAt - approvalAt <= TimeSpan.FromHours 24.0
        && match change.Action with
           | LifecycleMutation.PurgeLivePayload(_, validUntil) ->
               Sql.isUtcMicrosecond validUntil && expiresAt <= validUntil
           | _ -> true

    let private approveFresh
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
        =
        task {
            let! ready =
                CaseLifecycleApproveDecision.ready
                    connection
                    transaction
                    witness
                    context
                    projection
                    change
                    draftHash
                    instant

            match ready with
            | Error outcome -> return outcome
            | Ok() ->
                return!
                    CaseLifecycleApproveCommit.emit
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
        }

    let private approveUnderLock
        connection
        transaction
        witness
        (context: ActorCallContext)
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        approvalId
        expiresAt
        =
        task {
            let draft = CaseLifecycleCandidate.draft projection.CaseId change

            try
                let draftHash = SHA256.HashData(draft)
                let! existing = CaseLifecycleRead.approvalById connection transaction approvalId

                match existing with
                | Some stored ->
                    return
                        CaseLifecycleStoreSupport.replayApproval
                            witness
                            approvalId
                            change.EventId
                            projection.CaseId
                            draftHash
                            context.Binding.ActorId
                            expiresAt
                            (Claim.view projection.Claim).Version
                            projection.Sequence
                            stored
                | None ->
                    let! instant = Sql.databaseNow connection transaction

                    if not (approvalTime change instant expiresAt) then
                        return LifecycleWriteOutcome.Refused LifecycleRefusal.ApprovalMismatch
                    else
                        return!
                            approveFresh
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
            finally
                CaseLifecycleStoreSupport.clear draft
        }

    let private authorizedProjection connection transaction context (change: LifecycleChange) =
        task {
            let! revision =
                ActorGrantRead.lockRevision
                    connection
                    transaction
                    true
                    System.Threading.CancellationToken.None

            let! found =
                CaseLifecycleStoreSupport.lockCase
                    connection
                    transaction
                    change.CaseReference
                    change.EventId

            match found with
            | None -> return None
            | Some projection ->
                let! allowed =
                    CaseLifecycleStoreSupport.authorize
                        connection
                        transaction
                        revision
                        context
                        projection

                return if allowed then Some projection else None
        }

    let private transact
        dataSource
        witness
        (context: ActorCallContext)
        (change: LifecycleChange)
        approvalId
        expiresAt
        =
        task {
            use! connection = RuntimeDatabase.openConnectionAsync dataSource

            use! _authorityLease =
                AuthorityOperationFence.acquireShared
                    (Some dataSource)
                    connection
                    System.Threading.CancellationToken.None

            use transaction = CaseLifecycleStoreSupport.beginTransaction connection

            let! found = authorizedProjection connection transaction context change

            match found with
            | None -> return LifecycleWriteOutcome.ResourceUnavailable
            | Some projection ->
                return!
                    approveUnderLock
                        connection
                        transaction
                        witness
                        context
                        projection
                        change
                        approvalId
                        expiresAt
        }

    let private approvalIdentity (context: ActorCallContext) (change: LifecycleChange) approvalId =
        let action =
            match change.Action with
            | LifecycleMutation.PurgeLivePayload _ -> EndpointAction.ApproveErasure
            | _ -> EndpointAction.ApproveLifecycle

        approvalId <> Guid.Empty
        && approvalId <> change.EventId
        && context.Action = action
        && PrincipalKey.isHuman context.Binding.Principal

    let approve
        dataSource
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (change: LifecycleChange)
        (approvalId: Guid)
        (expiresAt: DateTimeOffset)
        =
        task {
            let utcExpiry = CaseLifecycleStoreSupport.validInstant expiresAt
            let expiresAt = CaseLifecycleStoreSupport.microsecondInstant expiresAt

            if
                not (CaseLifecycleStoreSupport.validChange change)
                || not utcExpiry
                || not (approvalIdentity context change approvalId)
            then
                return LifecycleWriteOutcome.Refused LifecycleRefusal.ApprovalMismatch
            else
                try
                    witness.Admit()
                    return! transact dataSource witness context change approvalId expiresAt
                with
                | WitnessPending -> return LifecycleWriteOutcome.Unconfirmed approvalId
                | :? System.IO.InvalidDataException ->
                    return LifecycleWriteOutcome.Failed CoreFault.StoreIntegrityError
                | _ -> return LifecycleWriteOutcome.Failed CoreFault.StoreUnavailable
        }
