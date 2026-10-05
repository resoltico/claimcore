namespace ClaimCore.Postgres

open System
open System.Threading
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
        ct
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
                    ct

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
                        ct
        }

    let private replay
        witness
        approvalId
        (change: LifecycleChange)
        (projection: LifecycleProjection)
        draftHash
        (context: ActorCallContext)
        expiresAt
        stored
        ct
        =
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
            ct

    let private approveUnderLock
        connection
        transaction
        witness
        (context: ActorCallContext)
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        approvalId
        expiresAt
        ct
        =
        task {
            let draft = CaseLifecycleCandidate.draft projection.CaseId change

            try
                let draftHash = SHA256.HashData(draft)
                let! existing = CaseLifecycleRead.approvalById connection transaction approvalId

                match existing with
                | Some stored ->
                    return!
                        replay
                            witness
                            approvalId
                            change
                            projection
                            draftHash
                            context
                            expiresAt
                            stored
                            ct
                | None ->
                    let! instant = Sql.databaseNow connection transaction ct

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
                                ct
            finally
                CaseLifecycleStoreSupport.clear draft
        }

    let private authorizedProjection connection transaction context (change: LifecycleChange) ct =
        task {
            let! revision = ActorGrantRead.lockRevision connection transaction true ct

            let! found =
                CaseLifecycleStoreSupport.lockCase
                    connection
                    transaction
                    change.CaseReference
                    change.EventId
                    ct

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
        ct
        =
        task {
            use! connection = RuntimeDatabase.openConnectionAsyncWithCancellation dataSource ct

            use! _authorityLease =
                AuthorityOperationFence.acquireShared (Some dataSource) connection ct

            use! transaction = CaseLifecycleStoreSupport.beginTransaction connection ct

            let! found = authorizedProjection connection transaction context change ct

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
                        ct
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
        ct
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
                    do! witness.Admit(ct)
                    return! transact dataSource witness context change approvalId expiresAt ct
                with
                | :? System.OperationCanceledException when ct.IsCancellationRequested ->
                    return LifecycleWriteOutcome.CancelledBeforeAdmission approvalId
                | WitnessPending -> return LifecycleWriteOutcome.Unconfirmed approvalId
                | :? System.IO.InvalidDataException ->
                    return LifecycleWriteOutcome.Failed CoreFault.StoreIntegrityError
                | _ -> return LifecycleWriteOutcome.Failed CoreFault.StoreUnavailable
        }
