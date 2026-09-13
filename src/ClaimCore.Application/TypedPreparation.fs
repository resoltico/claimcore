namespace ClaimCore.Application

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.RecordFormat

[<NoEquality; NoComparison>]
type internal PreviewResult =
    | Previewed of AdvisoryReview
    | PreviewRejected of Rejection
    | PreviewFailed of CoreFault

module internal TypedPreparation =
    let idempotencyConflict: Rejection =
        {
            Code = RejectionCode.IdempotencyConflict
            Message = "This operation ID belongs to different command content. Do not reuse it."
            Field = None
            ActualVersion = None
            Action = RecommendedAction.StopAndInvestigate
        }

    let private preparationDraft
        (request: CommandRequest)
        (canonical: byte array)
        (requestSha256: string)
        : RecoveryPreparationDraft =
        {
            OperationId = request.OperationId
            CanonicalRequestFormat = RecordVersions.CanonicalCommandFormat
            RequestSha256 = requestSha256
            CanonicalRequest = canonical
            PreparingApplicationVersion = BuildIdentity.current.Version
            PreparingContractFingerprint =
                SemanticContract.fingerprint SemanticContract.current
                |> SemanticCoreFingerprint.value
            PreparingContractKind = PreparingContractKind.SemanticCoreV1
        }

    let private preview
        (store: IClaimStore)
        (clock: IBusinessDate)
        (request: CommandRequest)
        : Task<PreviewResult> =
        task {
            match! store.Get request.CaseReference with
            | Error failure -> return PreviewFailed(TypedProjection.coreFault failure)
            | Ok current ->
                match Claim.decide (clock.Today()) request current with
                | Error rejection -> return PreviewRejected(TypedProjection.rejection rejection)
                | Ok proposed -> return Previewed(TypedProjection.review clock current proposed)
        }

    let private exactMaterial
        (canonical: byte array)
        (digest: string)
        (retained: RetainedPreparation)
        =
        retained.CanonicalRequestFormat = RecordVersions.CanonicalCommandFormat
        && retained.RequestSha256 = digest
        && CryptographicOperations.FixedTimeEquals(
            ReadOnlySpan<byte>(retained.CanonicalRequest),
            ReadOnlySpan<byte>(canonical)
        )

    let private replayFault: CoreFault =
        {
            Code = FaultCode.RecoveryIntegrityError
            Message = "An exact retained preparation could not be verified."
            Action = RecommendedAction.StopAndInvestigate
        }

    let private observedOutcome
        (store: IClaimStore)
        (clock: IBusinessDate)
        (request: CommandRequest)
        (retained: RetainedPreparation)
        (details: PreparationDetails)
        (cancellationToken: CancellationToken)
        : Task<PrepareOutcome> =
        task {
            let! verified =
                ObservedReceiptVerification.verify
                    store
                    clock
                    retained
                    details.Summary
                    cancellationToken

            match verified with
            | RetainedResolution.ObservedReceipt receipt ->
                return
                    PrepareOutcome.ObservedAccepted(
                        TypedProjection.acceptedDetails details,
                        receipt
                    )
            | RetainedResolution.ReceiptIdentityConflict _ ->
                return PrepareOutcome.PrepareRejected(request.OperationId, idempotencyConflict)
            | RetainedResolution.ResolutionCancelledBeforeAttempt _ ->
                return PrepareOutcome.CancelledBeforeAdmission request.OperationId
            | RetainedResolution.ResolutionFailedBeforeAttempt(_, fault) ->
                return PrepareOutcome.PrepareFailed(request.OperationId, fault)
            | _ -> return PrepareOutcome.PrepareFailed(request.OperationId, replayFault)
        }

    let private knownRetained
        (store: IClaimStore)
        (clock: IBusinessDate)
        (request: CommandRequest)
        (retained: RetainedPreparation)
        (cancellationToken: CancellationToken)
        : Task<PrepareOutcome> =
        task {
            match TypedProjection.details retained with
            | Error fault -> return PrepareOutcome.PrepareFailed(request.OperationId, fault)
            | Ok details ->
                let! observed = store.Operation request.OperationId

                if cancellationToken.IsCancellationRequested then
                    return PrepareOutcome.CancelledBeforeAdmission request.OperationId
                else
                    match observed with
                    | Error failure ->
                        return
                            PrepareOutcome.PrepareFailed(
                                request.OperationId,
                                TypedProjection.coreFault failure
                            )
                    | Ok(Some _) ->
                        return!
                            observedOutcome store clock request retained details cancellationToken
                    | Ok None ->
                        match! preview store clock request with
                        | Previewed review -> return PrepareOutcome.Prepared(details, review)
                        | PreviewRejected reason ->
                            return PrepareOutcome.RetainedForRecovery(details, reason)
                        | PreviewFailed fault ->
                            return PrepareOutcome.PrepareFailed(request.OperationId, fault)
        }

    let private existingOutcome
        (store: IClaimStore)
        (recovery: IRecoveryStore)
        (clock: IBusinessDate)
        (request: CommandRequest)
        (canonical: byte array)
        (digest: string)
        (cancellationToken: CancellationToken)
        : Task<PrepareOutcome option> =
        task {
            match! recovery.Get(request.OperationId, cancellationToken) with
            | Error RecoveryStoreFailure.ReadCancelled ->
                return Some(PrepareOutcome.CancelledBeforeAdmission request.OperationId)
            | Error failure ->
                return
                    Some(
                        PrepareOutcome.PrepareFailed(
                            request.OperationId,
                            TypedProjection.recoveryFault failure
                        )
                    )
            | Ok None -> return None
            | Ok(Some retained) when not (exactMaterial canonical digest retained) ->
                return
                    Some(PrepareOutcome.PrepareRejected(request.OperationId, idempotencyConflict))
            | Ok(Some retained) ->
                let! outcome = knownRetained store clock request retained cancellationToken
                return Some outcome
        }

    let private retainedOutcome
        (store: IClaimStore)
        (recovery: IRecoveryStore)
        (clock: IBusinessDate)
        (request: CommandRequest)
        (review: AdvisoryReview)
        (canonical: byte array)
        (requestSha256: string)
        (cancellationToken: CancellationToken)
        : Task<PrepareOutcome> =
        task {
            match!
                recovery.Retain(preparationDraft request canonical requestSha256, cancellationToken)
            with
            | Ok(RecoveryRetain.Created retained) ->
                match TypedProjection.details retained with
                | Ok details -> return PrepareOutcome.Prepared(details, review)
                | Error fault -> return PrepareOutcome.PrepareFailed(request.OperationId, fault)
            | Ok(RecoveryRetain.Existing retained) when
                not (exactMaterial canonical requestSha256 retained)
                ->
                return PrepareOutcome.PrepareRejected(request.OperationId, idempotencyConflict)
            | Ok(RecoveryRetain.Existing retained) ->
                return! knownRetained store clock request retained cancellationToken
            | Error RecoveryStoreFailure.IdempotencyConflict ->
                return PrepareOutcome.PrepareRejected(request.OperationId, idempotencyConflict)
            | Error RecoveryStoreFailure.TechnicalMutationUnknown ->
                return
                    PrepareOutcome.PreparationStateUnknown(
                        request.OperationId,
                        requestSha256,
                        TypedProjection.recoveryFault RecoveryStoreFailure.TechnicalMutationUnknown
                    )
            | Error RecoveryStoreFailure.CancelledBeforeCommit ->
                return PrepareOutcome.CancelledBeforeAdmission request.OperationId
            | Error failure ->
                return
                    PrepareOutcome.PrepareFailed(
                        request.OperationId,
                        TypedProjection.recoveryFault failure
                    )
        }

    let private freshOutcome
        (store: IClaimStore)
        (recovery: IRecoveryStore)
        (clock: IBusinessDate)
        (request: CommandRequest)
        (canonical: byte array)
        (digest: string)
        (cancellationToken: CancellationToken)
        : Task<PrepareOutcome> =
        task {
            match! preview store clock request with
            | PreviewFailed fault -> return PrepareOutcome.PrepareFailed(request.OperationId, fault)
            | PreviewRejected rejection ->
                match!
                    existingOutcome store recovery clock request canonical digest cancellationToken
                with
                | Some outcome -> return outcome
                | None -> return PrepareOutcome.PrepareRejected(request.OperationId, rejection)
            | Previewed _ when cancellationToken.IsCancellationRequested ->
                return PrepareOutcome.CancelledBeforeAdmission request.OperationId
            | Previewed review ->
                return!
                    retainedOutcome
                        store
                        recovery
                        clock
                        request
                        review
                        canonical
                        digest
                        cancellationToken
        }

    let prepare
        (store: IClaimStore)
        (recovery: IRecoveryStore)
        (clock: IBusinessDate)
        (draft: CommandDraft)
        (cancellationToken: CancellationToken)
        : Task<PrepareOutcome> =
        match Drafts.bind draft with
        | Error rejection ->
            Task.FromResult(
                PrepareOutcome.PrepareRejected(
                    draft.OperationId,
                    TypedProjection.rejection rejection
                )
            )
        | Ok request when cancellationToken.IsCancellationRequested ->
            Task.FromResult(PrepareOutcome.CancelledBeforeAdmission request.OperationId)
        | Ok request ->
            task {
                let canonical = RequestRecord.encode request
                let digest = canonical |> SHA256.HashData |> Convert.ToHexStringLower

                match!
                    existingOutcome store recovery clock request canonical digest cancellationToken
                with
                | Some outcome -> return outcome
                | None ->
                    return!
                        freshOutcome store recovery clock request canonical digest cancellationToken
            }
