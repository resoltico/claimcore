namespace ClaimCore.Application

open System.Threading
open System.Threading.Tasks
open System.Security.Cryptography
open ClaimCore.RecordFormat

module internal TypedSubmission =
    let private integrityFailure (message: string) : CoreFault =
        {
            Code = FaultCode.RecoveryIntegrityError
            Message = message
            Action = RecommendedAction.StopAndInvestigate
        }

    let private preAttemptOutcome (result: RetainedResolution) : SubmissionOutcome =
        match result with
        | RetainedResolution.MissingPreparation _ ->
            SubmissionOutcome.FailedBeforeAttempt(
                None,
                integrityFailure "A newly retained preparation was not available for execution."
            )
        | RetainedResolution.DismissedPreparation summary ->
            SubmissionOutcome.FailedBeforeAttempt(
                Some summary,
                {
                    Code = FaultCode.TechnicalMutationUnknown
                    Message = "The preparation was dismissed before execution could begin."
                    Action = RecommendedAction.RecoverExact
                }
            )
        | RetainedResolution.DigestConflict summary ->
            SubmissionOutcome.FailedBeforeAttempt(
                Some summary,
                integrityFailure
                    "The retained preparation digest does not match the submitted draft."
            )
        | RetainedResolution.ReceiptIdentityConflict summary ->
            SubmissionOutcome.RejectedBeforeAttempt(
                Some summary,
                {
                    Code = RejectionCode.IdempotencyConflict
                    Message = "This operation ID belongs to different accepted command content."
                    Field = None
                    ActualVersion = None
                    Action = RecommendedAction.StopAndInvestigate
                }
            )
        | _ -> invalidOp "A pre-attempt result was expected."

    let private cancelledResolution knownPreparation operationId =
        match knownPreparation with
        | Some summary -> SubmissionOutcome.CancelledBeforeAttempt summary
        | None -> SubmissionOutcome.CancelledBeforeAdmission operationId

    let private resolvedOutcome
        (knownPreparation: PreparationSummary option)
        (result: RetainedResolution)
        : SubmissionOutcome =
        match result with
        | RetainedResolution.ObservedReceipt receipt -> SubmissionOutcome.ObservedAccepted receipt
        | RetainedResolution.Resolved(summary, attemptId, execution, settlement) ->
            SubmissionOutcome.Completed(summary, attemptId, execution, settlement)
        | RetainedResolution.MissingPreparation _
        | RetainedResolution.DismissedPreparation _
        | RetainedResolution.DigestConflict _
        | RetainedResolution.ReceiptIdentityConflict _ -> preAttemptOutcome result
        | RetainedResolution.ResolutionCancelledBeforeAdmission operationId ->
            cancelledResolution knownPreparation operationId
        | RetainedResolution.ResolutionFailedBeforeAttempt(summary, fault) ->
            SubmissionOutcome.FailedBeforeAttempt(summary, fault)
        | RetainedResolution.ResolutionCancelledBeforeAttempt summary ->
            SubmissionOutcome.CancelledBeforeAttempt summary
        | RetainedResolution.ResolutionAdmissionUnknown(summary, fault) ->
            SubmissionOutcome.AttemptAdmissionUnknown(summary, fault)
        | RetainedResolution.ResolutionUnresolved(summary, attemptId, fault) ->
            SubmissionOutcome.AttemptUnresolved(summary, attemptId, fault)

    let private completePrepared
        (store: IClaimStore)
        (recovery: IRecoveryStore)
        (clock: IBusinessDate)
        (draft: CommandDraft)
        (cancellationToken: CancellationToken)
        : Task<SubmissionOutcome> =
        task {
            match! TypedPreparation.prepare store recovery clock draft cancellationToken with
            | PrepareOutcome.Prepared(details, _) ->
                let digest = details.Summary.RequestSha256 |> Option.defaultValue ""

                let! result =
                    TypedResolution.resolveRetained
                        store
                        recovery
                        clock
                        details.Summary.OperationId
                        digest
                        cancellationToken

                return resolvedOutcome (Some details.Summary) result
            | PrepareOutcome.ObservedAccepted(_, receipt) ->
                return SubmissionOutcome.ObservedAccepted receipt
            | PrepareOutcome.RetainedForRecovery(details, reason) ->
                return SubmissionOutcome.RejectedBeforeAttempt(Some details.Summary, reason)
            | PrepareOutcome.PrepareRejected(_, rejection) ->
                return SubmissionOutcome.RejectedBeforeAttempt(None, rejection)
            | PrepareOutcome.PrepareFailed(_, fault) ->
                return SubmissionOutcome.FailedBeforeAttempt(None, fault)
            | PrepareOutcome.CancelledBeforeAdmission operationId ->
                return SubmissionOutcome.CancelledBeforeAdmission operationId
            | PrepareOutcome.PreparationStateUnknown(operationId, requestSha256, fault) ->
                return SubmissionOutcome.PreparationStateUnknown(operationId, requestSha256, fault)
        }

    let private retryOutcome
        (store: IClaimStore)
        (recovery: IRecoveryStore)
        (clock: IBusinessDate)
        (draft: CommandDraft)
        (cancellationToken: CancellationToken)
        =
        task {
            match Drafts.bind draft with
            | Error _ -> return None
            | Ok request ->
                let canonical = RequestRecord.encode request
                let digest = canonical |> SHA256.HashData |> System.Convert.ToHexStringLower

                match! recovery.Get(request.OperationId, cancellationToken) with
                | Ok(Some retained) when
                    retained.CanonicalRequestFormat = RecordVersions.CanonicalCommandFormat
                    && retained.RequestSha256 = digest
                    && CryptographicOperations.FixedTimeEquals(
                        System.ReadOnlySpan<byte>(retained.CanonicalRequest),
                        System.ReadOnlySpan<byte>(canonical)
                    )
                    ->
                    let! result =
                        TypedResolution.resolveRetained
                            store
                            recovery
                            clock
                            request.OperationId
                            digest
                            cancellationToken

                    let knownPreparation = TypedProjection.summary true retained |> Result.toOption

                    return Some(resolvedOutcome knownPreparation result)
                | Ok(Some retained) ->
                    let knownPreparation = TypedProjection.summary true retained |> Result.toOption

                    return
                        Some(
                            SubmissionOutcome.RejectedBeforeAttempt(
                                knownPreparation,
                                TypedPreparation.idempotencyConflict
                            )
                        )
                | Ok _ -> return None
                | Error _ -> return None
        }

    let execute
        (store: IClaimStore)
        (recovery: IRecoveryStore)
        (clock: IBusinessDate)
        (draft: CommandDraft)
        (cancellationToken: CancellationToken)
        : Task<SubmissionOutcome> =
        task {
            if cancellationToken.IsCancellationRequested then
                return! completePrepared store recovery clock draft cancellationToken
            else
                match! retryOutcome store recovery clock draft cancellationToken with
                | Some outcome -> return outcome
                | None -> return! completePrepared store recovery clock draft cancellationToken
        }
