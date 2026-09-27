namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain

module internal CaseLifecycleApprove =
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
        instant
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

    let private transact
        dataSource
        witness
        (context: ActorCallContext)
        (change: LifecycleChange)
        approvalId
        expiresAt
        instant
        =
        task {
            use! connection = RuntimeDatabase.openConnectionAsync dataSource
            use transaction = CaseLifecycleStoreSupport.beginTransaction connection

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
            | None -> return LifecycleWriteOutcome.ResourceUnavailable
            | Some projection ->
                let! allowed =
                    CaseLifecycleStoreSupport.authorize
                        connection
                        transaction
                        revision
                        context
                        projection

                if not allowed then
                    return LifecycleWriteOutcome.ResourceUnavailable
                else
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
                            instant
        }

    let private approvalTime (change: LifecycleChange) approvalAt expiresAt =
        CaseLifecycleStoreSupport.validInstant approvalAt
        && CaseLifecycleStoreSupport.validInstant expiresAt
        && expiresAt > approvalAt
        && expiresAt - approvalAt <= TimeSpan.FromHours 24.0
        && match change.Action with
           | LifecycleMutation.PurgeLivePayload(_, validUntil) -> expiresAt <= validUntil
           | _ -> true

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
        (instant: DateTimeOffset)
        =
        task {
            let validTime = approvalTime change instant expiresAt

            let instant = CaseLifecycleStoreSupport.microsecondInstant instant
            let expiresAt = CaseLifecycleStoreSupport.microsecondInstant expiresAt

            if
                not (CaseLifecycleStoreSupport.validChange change)
                || not validTime
                || not (approvalIdentity context change approvalId)
            then
                return LifecycleWriteOutcome.Refused LifecycleRefusal.ApprovalMismatch
            else
                try
                    witness.Admit()
                    return! transact dataSource witness context change approvalId expiresAt instant
                with
                | WitnessPending -> return LifecycleWriteOutcome.Unconfirmed approvalId
                | :? System.IO.InvalidDataException ->
                    return LifecycleWriteOutcome.Failed CoreFault.StoreIntegrityError
                | _ -> return LifecycleWriteOutcome.Failed CoreFault.StoreUnavailable
        }
