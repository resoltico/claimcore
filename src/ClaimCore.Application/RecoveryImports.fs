namespace ClaimCore.Application

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks

module internal RecoveryImports =
    let private sourceMatches (sourceSha256: string) (source: byte array) =
        RecoverySupport.validDigest sourceSha256
        && (source |> SHA256.HashData |> Convert.ToHexStringLower) = sourceSha256

    let private retain
        (recovery: IRecoveryStore)
        (preview: RecoveryImportPreview)
        (sourceSha256: string)
        (canonical: byte array)
        (cancellationToken: CancellationToken)
        : Task<RecoveryImportRetainOutcome> =
        task {
            match!
                recovery.Retain(
                    RecoverySupport.preparationDraft
                        preview.ArtifactKind
                        preview.DecodedEffect
                        canonical,
                    cancellationToken
                )
            with
            | Ok(RecoveryRetain.Created value) ->
                match TypedProjection.details value with
                | Ok details -> return RecoveryImportRetainOutcome.RetainedPreparation details
                | Error fault -> return RecoveryImportRetainOutcome.ImportFailed fault
            | Ok(RecoveryRetain.Existing value) ->
                match TypedProjection.details value with
                | Ok details -> return RecoveryImportRetainOutcome.ExistingPreparation details
                | Error fault -> return RecoveryImportRetainOutcome.ImportFailed fault
            | Ok(RecoveryRetain.ObservedAccepted receipt) ->
                return
                    RecoveryImportRetainOutcome.ObservedAcceptedImport(
                        TypedProjection.receipt receipt
                    )
            | Ok(RecoveryRetain.Revoked _) ->
                return RecoveryImportRetainOutcome.ImportRejected RecoverySupport.revoked
            | Error RecoveryStoreFailure.IdempotencyConflict ->
                return RecoveryImportRetainOutcome.ImportRejected RecoverySupport.conflict
            | Error RecoveryStoreFailure.TechnicalMutationUnknown ->
                return
                    RecoveryImportRetainOutcome.RetainStateUnknown(
                        preview.ArtifactKind,
                        sourceSha256,
                        Some preview.DecodedEffect.OperationId,
                        TypedProjection.recoveryFault RecoveryStoreFailure.TechnicalMutationUnknown
                    )
            | Error RecoveryStoreFailure.CancelledBeforeCommit ->
                return RecoveryImportRetainOutcome.ImportCancelledBeforeAdmission
            | Error failure ->
                return
                    RecoveryImportRetainOutcome.ImportFailed(TypedProjection.recoveryFault failure)
        }

    let private retainPreview
        (recovery: IRecoveryStore)
        (sourceSha256: string)
        (cancellationToken: CancellationToken)
        (preview: RecoveryImportPreview)
        (canonical: byte array)
        : Task<RecoveryImportRetainOutcome> =
        task {
            if cancellationToken.IsCancellationRequested then
                return RecoveryImportRetainOutcome.ImportCancelledBeforeAdmission
            else
                return! retain recovery preview sourceSha256 canonical cancellationToken
        }

    let previewEnvelope
        (recovery: IRecoveryStore)
        (source: byte array)
        (cancellationToken: CancellationToken)
        : Task<RecoveryQueryOutcome<RecoveryImportPreview>> =
        if cancellationToken.IsCancellationRequested then
            Task.FromResult(RecoveryQueryOutcome.RecoveryCancelled)
        else
            task {
                match! RecoverySupport.decodeEnvelope recovery source cancellationToken with
                | RecoverySupport.ImportRefused rejection ->
                    return RecoveryQueryOutcome.RecoveryRejected rejection
                | RecoverySupport.ImportFailed fault ->
                    return RecoveryQueryOutcome.RecoveryFailed fault
                | RecoverySupport.ImportCancelled -> return RecoveryQueryOutcome.RecoveryCancelled
                | RecoverySupport.Imported preview ->
                    return! RecoverySupport.attachExisting recovery preview cancellationToken
            }

    let previewCanonical
        (recovery: IRecoveryStore)
        (source: byte array)
        (cancellationToken: CancellationToken)
        : Task<RecoveryQueryOutcome<RecoveryImportPreview>> =
        if cancellationToken.IsCancellationRequested then
            Task.FromResult(RecoveryQueryOutcome.RecoveryCancelled)
        else
            match RecoverySupport.decodeCanonical source with
            | Error rejection -> Task.FromResult(RecoveryQueryOutcome.RecoveryRejected rejection)
            | Ok preview -> RecoverySupport.attachExisting recovery preview cancellationToken

    let retainEnvelope
        (recovery: IRecoveryStore)
        (source: byte array)
        (sourceSha256: string)
        (cancellationToken: CancellationToken)
        : Task<RecoveryImportRetainOutcome> =
        task {
            if cancellationToken.IsCancellationRequested then
                return RecoveryImportRetainOutcome.ImportCancelledBeforeAdmission
            elif not (sourceMatches sourceSha256 source) then
                return RecoveryImportRetainOutcome.ImportRejected RecoverySupport.digestMismatch
            else
                match! RecoverySupport.decodeEnvelope recovery source cancellationToken with
                | RecoverySupport.ImportRefused rejection ->
                    return RecoveryImportRetainOutcome.ImportRejected rejection
                | RecoverySupport.ImportFailed fault ->
                    return RecoveryImportRetainOutcome.ImportFailed fault
                | RecoverySupport.ImportCancelled ->
                    return RecoveryImportRetainOutcome.ImportCancelledBeforeAdmission
                | RecoverySupport.Imported preview ->
                    match RecoverySupport.importedCanonical preview.ArtifactKind source with
                    | Error rejection -> return RecoveryImportRetainOutcome.ImportRejected rejection
                    | Ok canonical ->
                        return!
                            retainPreview recovery sourceSha256 cancellationToken preview canonical
        }

    let retainCanonical
        (recovery: IRecoveryStore)
        (source: byte array)
        (sourceSha256: string)
        (cancellationToken: CancellationToken)
        : Task<RecoveryImportRetainOutcome> =
        task {
            if cancellationToken.IsCancellationRequested then
                return RecoveryImportRetainOutcome.ImportCancelledBeforeAdmission
            elif not (sourceMatches sourceSha256 source) then
                return RecoveryImportRetainOutcome.ImportRejected RecoverySupport.digestMismatch
            else
                match RecoverySupport.decodeCanonical source with
                | Error rejection -> return RecoveryImportRetainOutcome.ImportRejected rejection
                | Ok preview ->
                    return! retainPreview recovery sourceSha256 cancellationToken preview source
        }
