namespace ClaimCore.Application

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks

module internal RecoveryImports =
    let private sourceMatches (sourceSha256: string) (source: byte array) =
        RecoverySupport.validDigest sourceSha256
        && (source |> SHA256.HashData |> Convert.ToHexStringLower) = sourceSha256

    let private retainedResult (preview: RecoveryImportPreview) sourceSha256 =
        function
        | Ok(RecoveryRetain.Created value) ->
            TypedProjection.details value
            |> Result.map RecoveryImportRetainOutcome.RetainedPreparation
            |> Result.defaultWith RecoveryImportRetainOutcome.ImportFailed
        | Ok(RecoveryRetain.Existing value) ->
            TypedProjection.details value
            |> Result.map RecoveryImportRetainOutcome.ExistingPreparation
            |> Result.defaultWith RecoveryImportRetainOutcome.ImportFailed
        | Ok(RecoveryRetain.ObservedAccepted receipt) ->
            RecoveryImportRetainOutcome.ObservedAcceptedImport(TypedProjection.receipt receipt)
        | Ok(RecoveryRetain.Revoked _) ->
            RecoveryImportRetainOutcome.ImportRejected RecoverySupport.revoked
        | Error RecoveryStoreFailure.IdempotencyConflict ->
            RecoveryImportRetainOutcome.ImportRejected RecoverySupport.conflict
        | Error RecoveryStoreFailure.ResourceUnavailable ->
            RecoveryImportRetainOutcome.ImportRejected RecoveryRejection.ResourceUnavailable
        | Error RecoveryStoreFailure.TechnicalMutationUnknown ->
            RecoveryImportRetainOutcome.RetainStateUnknown(
                preview.ArtifactKind,
                sourceSha256,
                Some preview.DecodedEffect.OperationId,
                TypedProjection.recoveryFault RecoveryStoreFailure.TechnicalMutationUnknown
            )
        | Error RecoveryStoreFailure.CancelledBeforeCommit ->
            RecoveryImportRetainOutcome.ImportCancelledBeforeAdmission
        | Error failure ->
            RecoveryImportRetainOutcome.ImportFailed(TypedProjection.recoveryFault failure)

    let private retain
        (recovery: IRecoveryStore)
        (importer: ActorBinding)
        (verified: VerifiedRecoveryArtifact)
        (preview: RecoveryImportPreview)
        (sourceSha256: string)
        (cancellationToken: CancellationToken)
        : Task<RecoveryImportRetainOutcome> =
        task {
            if importer.ActorId = Guid.Empty || importer.GrantRevision < 0L then
                return
                    RecoveryImportRetainOutcome.ImportRejected RecoveryRejection.ResourceUnavailable
            else
                let! result =
                    recovery.Retain(
                        RecoverySupport.preparationDraft verified importer verified.CanonicalRequest,
                        cancellationToken
                    )

                return retainedResult preview sourceSha256 result
        }

    let private retainPreview
        (recovery: IRecoveryStore)
        (importer: ActorBinding)
        (verified: VerifiedRecoveryArtifact)
        (sourceSha256: string)
        (cancellationToken: CancellationToken)
        (preview: RecoveryImportPreview)
        : Task<RecoveryImportRetainOutcome> =
        task {
            if cancellationToken.IsCancellationRequested then
                return RecoveryImportRetainOutcome.ImportCancelledBeforeAdmission
            else
                return! retain recovery importer verified preview sourceSha256 cancellationToken
        }

    let previewEnvelope
        (authority: IRecoveryArtifactAuthority)
        (source: byte array)
        (cancellationToken: CancellationToken)
        : Task<RecoveryQueryOutcome<RecoveryImportPreview>> =
        if cancellationToken.IsCancellationRequested then
            Task.FromResult(RecoveryQueryOutcome.RecoveryCancelled)
        else
            task {
                match! RecoverySupport.decodeEnvelope authority source cancellationToken with
                | RecoverySupport.ImportRefused rejection ->
                    return RecoveryQueryOutcome.RecoveryRejected rejection
                | RecoverySupport.ImportFailed fault ->
                    return RecoveryQueryOutcome.RecoveryFailed fault
                | RecoverySupport.ImportCancelled -> return RecoveryQueryOutcome.RecoveryCancelled
                | RecoverySupport.Imported(preview, verified) ->
                    CryptographicOperations.ZeroMemory(verified.CanonicalRequest)
                    return RecoveryQueryOutcome.RecoverySucceeded preview
            }

    let retainEnvelope
        (recovery: IRecoveryStore)
        (authority: IRecoveryArtifactAuthority)
        (importer: ActorBinding)
        (source: byte array)
        (sourceSha256: string)
        (cancellationToken: CancellationToken)
        : Task<RecoveryImportRetainOutcome> =
        task {
            if cancellationToken.IsCancellationRequested then
                return RecoveryImportRetainOutcome.ImportCancelledBeforeAdmission
            elif not (sourceMatches sourceSha256 source) then
                return
                    RecoveryImportRetainOutcome.ImportRejected
                        RecoveryRejection.SourceDigestMismatch
            else
                match! RecoverySupport.decodeEnvelope authority source cancellationToken with
                | RecoverySupport.ImportRefused rejection ->
                    return RecoveryImportRetainOutcome.ImportRejected rejection
                | RecoverySupport.ImportFailed fault ->
                    return RecoveryImportRetainOutcome.ImportFailed fault
                | RecoverySupport.ImportCancelled ->
                    return RecoveryImportRetainOutcome.ImportCancelledBeforeAdmission
                | RecoverySupport.Imported(preview, verified) ->
                    try
                        return!
                            retainPreview
                                recovery
                                importer
                                verified
                                sourceSha256
                                cancellationToken
                                preview
                    finally
                        CryptographicOperations.ZeroMemory(verified.CanonicalRequest)
        }
