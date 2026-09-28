namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open OperationAuthorityStore
open PreparationData
open RecoveryExecutionSupport
open RecoveryExecutionOutcomes

module internal RecoveryExecutionDecision =
    let private decideCurrent
        connection
        transaction
        operation
        attemptId
        (capture: unit -> BusinessContext)
        decide
        cancellationToken
        commitStarted
        knownRejection
        witness
        current
        retained
        actorContext
        =
        task {
            let context = capture ()

            match decide context.EffectiveBusinessDate current with
            | Error rejection ->
                return!
                    rejectPending
                        connection
                        transaction
                        attemptId
                        rejection
                        cancellationToken
                        commitStarted
                        knownRejection
            | Ok claim ->
                return!
                    acceptPending
                        connection
                        transaction
                        operation
                        context
                        current
                        claim
                        retained
                        actorContext
                        attemptId
                        cancellationToken
                        commitStarted
                        witness
        }

    let private currentAuthorized
        connection
        transaction
        (request: CommandRequest)
        (retained: RetainedPreparation)
        (actorContext: ActorCallContext)
        revision
        =
        task {
            do! Sql.lockKeyAsync connection transaction ("case:" + request.CaseReference)
            let! current = StoreData.readCase connection transaction request.CaseReference

            let! caseId =
                StoreData.caseIdForDecision
                    connection
                    transaction
                    request.CaseReference
                    current
                    (Some retained.CaseId)
                    (match request.Command with
                     | Command.Open _ -> true
                     | _ -> false)

            let! allowed =
                ActorMutationGuard.authorize
                    connection
                    transaction
                    actorContext
                    request
                    caseId
                    revision

            return if allowed then Some current else None
        }

    let private afterRetained
        connection
        transaction
        operation
        attemptId
        capture
        decide
        cancellationToken
        commitStarted
        knownRejection
        (actorContext: ActorCallContext)
        revision
        witness
        (retained: RetainedPreparation)
        =
        task {
            let request = Operation.request operation
            let! admitted = attemptExists connection transaction request.OperationId attemptId

            if not admitted then
                return Error RecoveryStoreFailure.NotFound
            else
                let! current =
                    currentAuthorized connection transaction request retained actorContext revision

                match current with
                | None -> return Error RecoveryStoreFailure.ResourceUnavailable
                | Some value ->
                    return!
                        decideCurrent
                            connection
                            transaction
                            operation
                            attemptId
                            capture
                            decide
                            cancellationToken
                            commitStarted
                            knownRejection
                            witness
                            value
                            retained
                            actorContext
        }

    let private executePending
        connection
        transaction
        operation
        attemptId
        (capture: unit -> BusinessContext)
        decide
        (cancellationToken: CancellationToken)
        commitStarted
        knownRejection
        (actorContext: ActorCallContext)
        revision
        witness
        =
        task {
            let request = Operation.request operation
            let! preparation = readHeader connection (Some transaction) request.OperationId

            match preparation with
            | None -> return Error RecoveryStoreFailure.NotFound
            | Some retained when retained.RequestSha256 <> Operation.fingerprint operation ->
                return Error RecoveryStoreFailure.IdempotencyConflict
            | Some retained ->
                return!
                    afterRetained
                        connection
                        transaction
                        operation
                        attemptId
                        capture
                        decide
                        cancellationToken
                        commitStarted
                        knownRejection
                        actorContext
                        revision
                        witness
                        retained
        }

    let executeWithoutAccepted
        connection
        transaction
        operation
        fingerprint
        attemptId
        (capture: unit -> BusinessContext)
        decide
        cancellationToken
        commitStarted
        knownRejection
        (knownRevocation: bool ref)
        (actorContext: ActorCallContext)
        revision
        witness
        =
        task {
            let request = Operation.request operation
            let! revoked = find connection transaction request.OperationId

            match revoked with
            | Some value when matches fingerprint value ->
                knownRevocation.Value <- true

                return!
                    revokeExisting
                        connection
                        transaction
                        request
                        attemptId
                        cancellationToken
                        commitStarted
                        witness
            | Some _ -> return Error RecoveryStoreFailure.IdempotencyConflict
            | None ->
                return!
                    executePending
                        connection
                        transaction
                        operation
                        attemptId
                        capture
                        decide
                        cancellationToken
                        commitStarted
                        knownRejection
                        actorContext
                        revision
                        witness
        }
