namespace ClaimCore.Postgres

open System
open System.Threading
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain

module internal CaseLifecycleApply =
    let private applyFresh
        connection
        transaction
        witness
        commitments
        context
        projection
        change
        draftHash
        instant
        ct
        =
        task {
            let! correct =
                CaseLifecycleStoreSupport.matchesWitness connection transaction witness

            if not correct then
                return LifecycleWriteOutcome.ResourceUnavailable
            else
                let! result, approvals =
                    CaseLifecycleApplyDecision.decide
                        connection
                        transaction
                        witness
                        commitments
                        projection
                        change
                        context.Binding.ActorId
                        draftHash
                        instant
                        ct

                match result with
                | Error refusal -> return LifecycleWriteOutcome.Refused refusal
                | Ok decision ->
                    return!
                        CaseLifecycleApplyCommit.emit
                            connection
                            transaction
                            witness
                            commitments
                            context
                            projection
                            change
                            draftHash
                            decision
                            approvals
                            instant
                            ct
        }

    let private applyUnderLock
        connection
        transaction
        witness
        commitments
        (context: ActorCallContext)
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        ct
        =
        task {
            let draft = CaseLifecycleCandidate.draft projection.CaseId change

            try
                let draftHash = SHA256.HashData(draft)
                let! existing = CaseLifecycleRead.eventById connection transaction change.EventId

                match existing with
                | Some stored ->
                    return!
                        CaseLifecycleStoreSupport.replayEvent
                            witness
                            change.EventId
                            draftHash
                            stored
                            ct
                | None when not (CaseLifecycleStoreSupport.matchesProjection change projection) ->
                    return LifecycleWriteOutcome.Refused LifecycleRefusal.VersionConflict
                | None ->
                    let! instant = Sql.databaseNow connection transaction ct

                    return!
                        applyFresh
                            connection
                            transaction
                            witness
                            commitments
                            context
                            projection
                            change
                            draftHash
                            instant
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
        commitments
        (context: ActorCallContext)
        (change: LifecycleChange)
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
                    applyUnderLock
                        connection
                        transaction
                        witness
                        commitments
                        context
                        projection
                        change
                        ct
        }

    let apply
        dataSource
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments)
        (context: ActorCallContext)
        (change: LifecycleChange)
        ct
        =
        task {
            if
                not (CaseLifecycleStoreSupport.validChange change)
                || context.Action <> CaseLifecycleStoreSupport.expectedAction change.Action
                || (match change.Action with
                    | LifecycleMutation.PurgeLivePayload _ -> true
                    | _ -> false)
            then
                return LifecycleWriteOutcome.Refused LifecycleRefusal.InvalidIdentity
            else
                try
                    do! witness.Admit(ct)
                    return! transact dataSource witness commitments context change ct
                with
                | :? System.OperationCanceledException when ct.IsCancellationRequested ->
                    return LifecycleWriteOutcome.CancelledBeforeAdmission change.EventId
                | WitnessPending -> return LifecycleWriteOutcome.Unconfirmed change.EventId
                | :? System.IO.InvalidDataException ->
                    return LifecycleWriteOutcome.Failed CoreFault.StoreIntegrityError
                | _ -> return LifecycleWriteOutcome.Failed CoreFault.StoreUnavailable
        }
