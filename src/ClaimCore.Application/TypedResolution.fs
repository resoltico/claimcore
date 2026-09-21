namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.RecordFormat

module internal TypedResolution =
    let private executionResult
        (summary: PreparationSummary)
        (attemptId: Guid)
        (request: ClaimCore.Domain.CommandRequest)
        (result: Result<AdmittedExecution, RecoveryStoreFailure>)
        : RetainedResolution =
        match result with
        | Ok(AdmittedExecution.Accepted accepted) ->
            Resolved(
                summary,
                attemptId,
                DefiniteExecution.Accepted(TypedProjection.receipt accepted),
                SettlementConfirmation.Confirmed
            )
        | Ok(AdmittedExecution.Rejected(rejection, settlement)) ->
            Resolved(
                summary,
                attemptId,
                DefiniteExecution.ExecutionRejected(
                    request.OperationId,
                    TypedProjection.rejection rejection
                ),
                settlement
            )
        | Ok(AdmittedExecution.RevokedBeforeExecution settlement) ->
            Resolved(
                summary,
                attemptId,
                DefiniteExecution.ExecutionRevokedBeforeExecution request.OperationId,
                settlement
            )
        | Ok(AdmittedExecution.FailedBeforeCommit(failure, settlement)) ->
            Resolved(
                summary,
                attemptId,
                DefiniteExecution.FailedBeforeCommit(
                    request.OperationId,
                    TypedProjection.coreFault failure
                ),
                settlement
            )
        | Ok(AdmittedExecution.CommitOutcomeUnknown _) ->
            ResolutionUnresolved(
                summary,
                attemptId,
                TypedProjection.coreFault (CoreFailure.CommitOutcomeUnknown request.OperationId)
            )
        | Error failure ->
            ResolutionUnresolved(summary, attemptId, TypedProjection.recoveryFault failure)

    let private executeClaim
        (recovery: IRecoveryStore)
        (clock: IBusinessTime)
        (preparation: RetainedPreparation)
        (summary: PreparationSummary)
        (attemptId: Guid)
        : Task<RetainedResolution> =
        task {
            match
                RequestRecord.decode
                    SemanticContract.current.RequestByteLimit
                    preparation.CanonicalRequest
            with
            | Error _ ->
                return
                    ResolutionFailedBeforeAttempt(Some summary, CoreFault.RetainedCanonicalInvalid)
            | Ok request ->
                match Operation.prepare request with
                | Error _ ->
                    return
                        ResolutionFailedBeforeAttempt(
                            Some summary,
                            CoreFault.RetainedDomainShapeInvalid
                        )
                | Ok operation ->
                    let! result =
                        recovery.ExecuteAdmitted(
                            operation,
                            attemptId,
                            (fun () -> (clock.Capture()).EffectiveBusinessDate),
                            (fun today current -> Claim.decide today request current),
                            CancellationToken.None
                        )

                    return executionResult summary attemptId request result
        }

    let private executeAdmitted
        (recovery: IRecoveryStore)
        (clock: IBusinessTime)
        (retained: RetainedPreparation)
        (summary: PreparationSummary)
        (attemptId: Guid)
        : Task<RetainedResolution> =
        task {
            let! execution =
                task {
                    try
                        return! executeClaim recovery clock retained summary attemptId
                    with _ ->
                        // Attempt admission succeeded. A thrown claim execution cannot
                        // prove whether its transaction reached the commit boundary.
                        return
                            ResolutionUnresolved(
                                summary,
                                attemptId,
                                TypedProjection.coreFault (
                                    CoreFailure.CommitOutcomeUnknown summary.OperationId
                                )
                            )
                }

            return execution
        }

    let private start
        (recovery: IRecoveryStore)
        (clock: IBusinessTime)
        (operationId: Guid)
        (summary: PreparationSummary)
        (cancellationToken: CancellationToken)
        : Task<RetainedResolution> =
        task {
            match! recovery.Start(operationId, cancellationToken) with
            | Error RecoveryStoreFailure.TechnicalMutationUnknown ->
                return
                    ResolutionAdmissionUnknown(
                        summary,
                        TypedProjection.recoveryFault RecoveryStoreFailure.TechnicalMutationUnknown
                    )
            | Error RecoveryStoreFailure.CancelledBeforeCommit ->
                return ResolutionCancelledBeforeAttempt summary
            | Error RecoveryStoreFailure.CapacityExceeded -> return AttemptLimitReached summary
            | Error failure ->
                return
                    ResolutionFailedBeforeAttempt(
                        Some summary,
                        TypedProjection.recoveryFault failure
                    )
            | Ok(RecoveryStart.Dismissed retained) ->
                match TypedProjection.summary true retained with
                | Ok revoked -> return RevokedPreparation(Some revoked)
                | Error fault -> return ResolutionFailedBeforeAttempt(Some summary, fault)
            | Ok(RecoveryStart.ObservedAccepted receipt) ->
                return ObservedReceipt(TypedProjection.receipt receipt)
            | Ok(RecoveryStart.Started(attemptId, retained))
            | Ok(RecoveryStart.AlreadyStarted(attemptId, retained)) ->
                match TypedProjection.summary true retained with
                | Error fault -> return ResolutionFailedBeforeAttempt(Some summary, fault)
                | Ok admitted -> return! executeAdmitted recovery clock retained admitted attemptId
        }

    let private resolvePrepared
        (store: IClaimStore)
        (recovery: IRecoveryStore)
        (clock: IBusinessTime)
        (operationId: Guid)
        (requestSha256: string)
        (preparation: RetainedPreparation)
        (cancellationToken: CancellationToken)
        : Task<RetainedResolution> =
        task {
            if preparation.RequestSha256 <> requestSha256 then
                return DigestConflict
            else
                match TypedProjection.summary true preparation with
                | Error fault -> return ResolutionFailedBeforeAttempt(None, fault)
                | Ok summary ->
                    let! observed = store.Operation operationId

                    match observed with
                    | Ok(Some _) ->
                        return! ObservedReceiptVerification.verify store clock preparation summary
                    | _ when cancellationToken.IsCancellationRequested ->
                        return ResolutionCancelledBeforeAttempt summary
                    | Error failure ->
                        return
                            ResolutionFailedBeforeAttempt(
                                Some summary,
                                TypedProjection.coreFault failure
                            )
                    | Ok None -> return! start recovery clock operationId summary cancellationToken
        }

    let resolveRetained
        (store: IClaimStore)
        (recovery: IRecoveryStore)
        (clock: IBusinessTime)
        (operationId: Guid)
        (requestSha256: string)
        (cancellationToken: CancellationToken)
        : Task<RetainedResolution> =
        task {
            if cancellationToken.IsCancellationRequested then
                return ResolutionCancelledBeforeAdmission operationId
            else
                match! recovery.Get(operationId, cancellationToken) with
                | Error RecoveryStoreFailure.ReadCancelled ->
                    return ResolutionCancelledBeforeAdmission operationId
                | Error failure ->
                    return
                        ResolutionFailedBeforeAttempt(None, TypedProjection.recoveryFault failure)
                | Ok None -> return MissingPreparation operationId
                | Ok(Some(RecoveryStoredOperation.RevokedTombstone revocation)) ->
                    if revocation.RequestSha256 = requestSha256 then
                        return RevokedPreparation None
                    else
                        return DigestConflict
                | Ok(Some(RecoveryStoredOperation.Retained(preparation, authority))) ->
                    if preparation.RequestSha256 <> requestSha256 then
                        return DigestConflict
                    elif authority = RecoveryAuthority.RevokedAuthority then
                        match
                            TypedProjection.summaryWithKnownAuthority true authority preparation
                        with
                        | Ok summary -> return RevokedPreparation(Some summary)
                        | Error fault -> return ResolutionFailedBeforeAttempt(None, fault)
                    else
                        return!
                            resolvePrepared
                                store
                                recovery
                                clock
                                operationId
                                requestSha256
                                preparation
                                cancellationToken
        }
