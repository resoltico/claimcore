namespace ClaimCore.Postgres

open System
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
        }

    let private applyUnderLock
        connection
        transaction
        witness
        commitments
        (context: ActorCallContext)
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        =
        task {
            let draft = CaseLifecycleCandidate.draft projection.CaseId change

            try
                let draftHash = SHA256.HashData(draft)
                let! existing = CaseLifecycleRead.eventById connection transaction change.EventId

                match existing with
                | Some stored ->
                    return
                        CaseLifecycleStoreSupport.replayEvent
                            witness
                            change.EventId
                            draftHash
                            stored
                | None when not (CaseLifecycleStoreSupport.matchesProjection change projection) ->
                    return LifecycleWriteOutcome.Refused LifecycleRefusal.VersionConflict
                | None ->
                    let! instant = Sql.databaseNow connection transaction

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
        commitments
        (context: ActorCallContext)
        (change: LifecycleChange)
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
                    applyUnderLock
                        connection
                        transaction
                        witness
                        commitments
                        context
                        projection
                        change
        }

    let apply
        dataSource
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments)
        (context: ActorCallContext)
        (change: LifecycleChange)
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
                    witness.Admit()
                    return! transact dataSource witness commitments context change
                with
                | WitnessPending -> return LifecycleWriteOutcome.Unconfirmed change.EventId
                | :? System.IO.InvalidDataException ->
                    return LifecycleWriteOutcome.Failed CoreFault.StoreIntegrityError
                | _ -> return LifecycleWriteOutcome.Failed CoreFault.StoreUnavailable
        }
