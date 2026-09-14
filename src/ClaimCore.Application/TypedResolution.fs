namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.RecordFormat

module internal TypedResolution =
    let private integrityFault (message: string) : CoreFault =
        {
            Code = FaultCode.RecoveryIntegrityError
            Message = message
            Action = RecommendedAction.StopAndInvestigate
        }

    let private settle
        (recovery: IRecoveryStore)
        (attemptId: Guid)
        (execution: DefiniteExecution)
        : Task<SettlementConfirmation> =
        let outcome =
            match execution with
            | DefiniteExecution.Accepted _ -> RecoverySettlement.Accepted
            | DefiniteExecution.ExecutionRejected _ -> RecoverySettlement.Rejected
            | DefiniteExecution.FailedBeforeCommit _ -> RecoverySettlement.FailedBeforeCommit

        task {
            try
                match! recovery.Settle(attemptId, outcome, CancellationToken.None) with
                | Ok() -> return SettlementConfirmation.Confirmed
                | Error _ -> return SettlementConfirmation.Unconfirmed
            with _ ->
                return SettlementConfirmation.Unconfirmed
        }

    let private executionResult
        (summary: PreparationSummary)
        (attemptId: Guid)
        (request: ClaimCore.Domain.CommandRequest)
        (result: Result<Receipt, CoreFailure>)
        : RetainedResolution =
        match result with
        | Ok accepted ->
            Resolved(
                summary,
                attemptId,
                DefiniteExecution.Accepted(TypedProjection.receipt accepted),
                SettlementConfirmation.Confirmed
            )
        | Error(CoreFailure.Domain rejection) ->
            Resolved(
                summary,
                attemptId,
                DefiniteExecution.ExecutionRejected(
                    request.OperationId,
                    TypedProjection.rejection rejection
                ),
                SettlementConfirmation.Confirmed
            )
        | Error(CoreFailure.CommitOutcomeUnknown _) ->
            ResolutionUnresolved(
                summary,
                attemptId,
                TypedProjection.coreFault (CoreFailure.CommitOutcomeUnknown request.OperationId)
            )
        | Error failure ->
            Resolved(
                summary,
                attemptId,
                DefiniteExecution.FailedBeforeCommit(
                    request.OperationId,
                    TypedProjection.coreFault failure
                ),
                SettlementConfirmation.Confirmed
            )

    let private executeClaim
        (store: IClaimStore)
        (clock: IBusinessDate)
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
                    ResolutionFailedBeforeAttempt(
                        Some summary,
                        integrityFault
                            "Retained canonical request bytes failed integrity validation."
                    )
            | Ok request ->
                let! result = Service.executeAsync store clock request
                return executionResult summary attemptId request result
        }

    let private executeAdmitted
        (store: IClaimStore)
        (recovery: IRecoveryStore)
        (clock: IBusinessDate)
        (retained: RetainedPreparation)
        (summary: PreparationSummary)
        (attemptId: Guid)
        : Task<RetainedResolution> =
        task {
            let! execution =
                task {
                    try
                        return! executeClaim store clock retained summary attemptId
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

            match execution with
            | Resolved(_, _, definite, _) ->
                let! settlement = settle recovery attemptId definite
                return Resolved(summary, attemptId, definite, settlement)
            | other -> return other
        }

    let private start
        (store: IClaimStore)
        (recovery: IRecoveryStore)
        (clock: IBusinessDate)
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
            | Error failure ->
                return
                    ResolutionFailedBeforeAttempt(
                        Some summary,
                        TypedProjection.recoveryFault failure
                    )
            | Ok(RecoveryStart.Dismissed retained) ->
                match TypedProjection.summary true retained with
                | Ok dismissed -> return DismissedPreparation dismissed
                | Error fault -> return ResolutionFailedBeforeAttempt(Some summary, fault)
            | Ok(RecoveryStart.Started(attemptId, retained))
            | Ok(RecoveryStart.AlreadyStarted(attemptId, retained)) ->
                match TypedProjection.summary true retained with
                | Error fault -> return ResolutionFailedBeforeAttempt(Some summary, fault)
                | Ok admitted ->
                    return! executeAdmitted store recovery clock retained admitted attemptId
        }

    let private resolvePrepared
        (store: IClaimStore)
        (recovery: IRecoveryStore)
        (clock: IBusinessDate)
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
                    | Ok None ->
                        return! start store recovery clock operationId summary cancellationToken
        }

    let resolveRetained
        (store: IClaimStore)
        (recovery: IRecoveryStore)
        (clock: IBusinessDate)
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
                | Ok(Some preparation) ->
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
