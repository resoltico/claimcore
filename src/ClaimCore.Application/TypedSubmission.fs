namespace ClaimCore.Application

open System.Threading
open System.Threading.Tasks
open System.Security.Cryptography
open ClaimCore.Domain
open ClaimCore.RecordFormat

module internal TypedSubmission =
    [<NoEquality; NoComparison>]
    type private RetainedRetry =
        | RetryNotFound
        | RetryRefused of SubmissionOutcome
        | RetryResolution of RetainedPreparation

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
        | RetainedResolution.RevokedPreparation summary ->
            SubmissionOutcome.RejectedBeforeAttempt(summary, OperationRejection.revoked)
        | RetainedResolution.AttemptLimitReached summary ->
            SubmissionOutcome.RejectedBeforeAttempt(Some summary, OperationRejection.attemptLimit)
        | RetainedResolution.DigestConflict ->
            SubmissionOutcome.FailedBeforeAttempt(
                None,
                integrityFailure
                    "The retained preparation digest does not match the submitted draft."
            )
        | RetainedResolution.ReceiptIdentityConflict ->
            SubmissionOutcome.RejectedBeforeAttempt(
                None,
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

    let private completedResolution =
        function
        | RetainedResolution.ObservedReceipt receipt ->
            Some(SubmissionOutcome.ObservedAccepted receipt)
        | RetainedResolution.Resolved(summary, attemptId, execution, settlement) ->
            Some(SubmissionOutcome.Completed(summary, attemptId, execution, settlement))
        | _ -> None

    let private preAttemptResolution =
        function
        | RetainedResolution.MissingPreparation _
        | RetainedResolution.DismissedPreparation _
        | RetainedResolution.RevokedPreparation _
        | RetainedResolution.AttemptLimitReached _
        | RetainedResolution.DigestConflict
        | RetainedResolution.ReceiptIdentityConflict as result -> Some(preAttemptOutcome result)
        | _ -> None

    let private interruptedResolution knownPreparation =
        function
        | RetainedResolution.ResolutionCancelledBeforeAdmission operationId ->
            Some(cancelledResolution knownPreparation operationId)
        | RetainedResolution.ResolutionFailedBeforeAttempt(summary, fault) ->
            Some(SubmissionOutcome.FailedBeforeAttempt(summary, fault))
        | RetainedResolution.ResolutionCancelledBeforeAttempt summary ->
            Some(SubmissionOutcome.CancelledBeforeAttempt summary)
        | RetainedResolution.ResolutionAdmissionUnknown(summary, fault) ->
            Some(SubmissionOutcome.AttemptAdmissionUnknown(summary, fault))
        | RetainedResolution.ResolutionUnresolved(summary, attemptId, fault) ->
            Some(SubmissionOutcome.AttemptUnresolved(summary, attemptId, fault))
        | _ -> None

    let private resolvedOutcome
        (knownPreparation: PreparationSummary option)
        (result: RetainedResolution)
        : SubmissionOutcome =
        completedResolution result
        |> Option.orElseWith (fun () -> preAttemptResolution result)
        |> Option.orElseWith (fun () -> interruptedResolution knownPreparation result)
        |> Option.defaultWith (fun () ->
            invalidOp "An unsupported recovery resolution was returned.")

    let private completePrepared
        (store: IClaimStore)
        (recovery: IRecoveryStore)
        (clock: IBusinessTime)
        (request: CommandRequest)
        (cancellationToken: CancellationToken)
        : Task<SubmissionOutcome> =
        task {
            match! TypedPreparation.prepare store recovery clock request cancellationToken with
            | PrepareOutcome.Prepared(details, _) ->
                // The authoritative digest, not the summary's: a recovery view may withhold that
                // one, and resolving under a substituted value would bind the wrong preparation.
                let _, digest = TypedPreparation.requestIdentity request

                let! result =
                    TypedResolution.resolveRetained
                        store
                        recovery
                        clock
                        details.Summary.OperationId
                        digest
                        cancellationToken

                return resolvedOutcome (Some details.Summary) result
            | PrepareOutcome.ObservedAccepted receipt ->
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

    let private exactMaterial
        (canonical: byte array)
        (digest: string)
        (retained: RetainedPreparation)
        =
        retained.CanonicalRequestFormat = RecordVersions.CanonicalCommandFormat
        && retained.RequestSha256 = digest
        && CryptographicOperations.FixedTimeEquals(
            System.ReadOnlySpan<byte>(retained.CanonicalRequest),
            System.ReadOnlySpan<byte>(canonical)
        )

    let private retainedRetryDisposition
        (canonical: byte array)
        (digest: string)
        (stored: RecoveryStoredOperation option)
        =
        match stored with
        | None -> RetryNotFound
        | Some(RecoveryStoredOperation.RevokedTombstone revocation) when
            revocation.RequestSha256 = digest
            ->
            RetryRefused(SubmissionOutcome.RejectedBeforeAttempt(None, OperationRejection.revoked))
        | Some(RecoveryStoredOperation.RevokedTombstone _) ->
            RetryRefused(
                SubmissionOutcome.RejectedBeforeAttempt(
                    None,
                    AcceptedObservation.idempotencyConflict
                )
            )
        | Some(RecoveryStoredOperation.Retained(retained, _)) when
            not (exactMaterial canonical digest retained)
            ->
            RetryRefused(
                SubmissionOutcome.RejectedBeforeAttempt(
                    None,
                    AcceptedObservation.idempotencyConflict
                )
            )
        | Some(RecoveryStoredOperation.Retained(_, RecoveryAuthority.RevokedAuthority)) ->
            RetryRefused(SubmissionOutcome.RejectedBeforeAttempt(None, OperationRejection.revoked))
        | Some(RecoveryStoredOperation.Retained(retained, _)) -> RetryResolution retained

    let private retainedRetry
        (store: IClaimStore)
        (recovery: IRecoveryStore)
        (clock: IBusinessTime)
        (request: CommandRequest)
        (canonical: byte array)
        digest
        (cancellationToken: CancellationToken)
        =
        task {
            match! recovery.Get(request.OperationId, cancellationToken) with
            | Error _ -> return None
            | Ok stored ->
                match retainedRetryDisposition canonical digest stored with
                | RetryNotFound -> return None
                | RetryRefused outcome -> return Some outcome
                | RetryResolution retained ->
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
        }

    let private retryOutcome
        (store: IClaimStore)
        (recovery: IRecoveryStore)
        (clock: IBusinessTime)
        (request: CommandRequest)
        (cancellationToken: CancellationToken)
        =
        task {
            match Claim.validateRequest request with
            | Error _ -> return None
            | Ok() ->
                let canonical = RequestRecord.encode request
                let digest = canonical |> SHA256.HashData |> System.Convert.ToHexStringLower

                match! store.Accepted(request.OperationId, digest) with
                | Ok(Some receipt) ->
                    return Some(SubmissionOutcome.ObservedAccepted(TypedProjection.receipt receipt))
                | Error CoreFailure.IdempotencyConflict ->
                    return
                        Some(
                            SubmissionOutcome.RejectedBeforeAttempt(
                                None,
                                AcceptedObservation.idempotencyConflict
                            )
                        )
                | Error failure ->
                    return
                        Some(
                            SubmissionOutcome.FailedBeforeAttempt(
                                None,
                                TypedProjection.coreFault failure
                            )
                        )
                | Ok None ->
                    return!
                        retainedRetry
                            store
                            recovery
                            clock
                            request
                            canonical
                            digest
                            cancellationToken
        }

    let execute
        (store: IClaimStore)
        (recovery: IRecoveryStore)
        (clock: IBusinessTime)
        (request: CommandRequest)
        (cancellationToken: CancellationToken)
        : Task<SubmissionOutcome> =
        task {
            if cancellationToken.IsCancellationRequested then
                return! completePrepared store recovery clock request cancellationToken
            else
                match! retryOutcome store recovery clock request cancellationToken with
                | Some outcome -> return outcome
                | None -> return! completePrepared store recovery clock request cancellationToken
        }
