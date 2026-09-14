namespace ClaimCore.Application

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.RecordFormat

module internal TypedPreparation =
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
                    Some(
                        PrepareOutcome.PrepareRejected(
                            request.OperationId,
                            AcceptedObservation.idempotencyConflict
                        )
                    )
            | Ok(Some retained) ->
                let! outcome =
                    RetainedPreparationReview.knownRetained
                        store
                        clock
                        request
                        retained
                        cancellationToken

                return Some outcome
        }

    let private retentionFailure operationId requestSha256 failure =
        match failure with
        | RecoveryStoreFailure.IdempotencyConflict ->
            PrepareOutcome.PrepareRejected(operationId, AcceptedObservation.idempotencyConflict)
        | RecoveryStoreFailure.TechnicalMutationUnknown ->
            PrepareOutcome.PreparationStateUnknown(
                operationId,
                requestSha256,
                TypedProjection.recoveryFault failure
            )
        | RecoveryStoreFailure.CancelledBeforeCommit ->
            PrepareOutcome.CancelledBeforeAdmission operationId
        | _ -> PrepareOutcome.PrepareFailed(operationId, TypedProjection.recoveryFault failure)

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
                return
                    PrepareOutcome.PrepareRejected(
                        request.OperationId,
                        AcceptedObservation.idempotencyConflict
                    )
            | Ok(RecoveryRetain.Existing retained) ->
                return!
                    RetainedPreparationReview.knownRetained
                        store
                        clock
                        request
                        retained
                        cancellationToken
            | Error failure -> return retentionFailure request.OperationId requestSha256 failure
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
            match! RetainedPreparationReview.preview store clock request with
            | PreviewFailed fault -> return PrepareOutcome.PrepareFailed(request.OperationId, fault)
            | PreviewRejected rejection ->
                match!
                    existingOutcome store recovery clock request canonical digest cancellationToken
                with
                | Some outcome -> return outcome
                | None ->
                    match! AcceptedObservation.prepare store request.OperationId digest with
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

                match! AcceptedObservation.prepare store request.OperationId digest with
                | Some outcome -> return outcome
                | None ->
                    match!
                        existingOutcome
                            store
                            recovery
                            clock
                            request
                            canonical
                            digest
                            cancellationToken
                    with
                    | Some outcome -> return outcome
                    | None ->
                        return!
                            freshOutcome
                                store
                                recovery
                                clock
                                request
                                canonical
                                digest
                                cancellationToken
            }
