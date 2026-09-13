namespace ClaimCore.Application

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.RecordFormat

module internal RecoverySupport =
    type ImportDecoding =
        | Imported of RecoveryImportPreview
        | ImportRefused of RecoveryRejection
        | ImportFailed of CoreFault
        | ImportCancelled

    let rejection code message action : RecoveryRejection =
        {
            Code = code
            Message = message
            Action = action
        }

    let invalid field =
        rejection
            RecoveryRejectionCode.InvalidRecoveryInput
            ($"The {field} value is invalid.")
            RecommendedAction.CorrectInput

    let notFound =
        rejection
            RecoveryRejectionCode.PreparationNotFound
            "The recovery preparation was not found."
            RecommendedAction.ReadCurrent

    let conflict =
        rejection
            RecoveryRejectionCode.RecoveryIdempotencyConflict
            "The supplied recovery identity conflicts with retained request material."
            RecommendedAction.StopAndInvestigate

    let dismissed =
        rejection
            RecoveryRejectionCode.PreparationDismissed
            "A dismissed preparation cannot be submitted."
            RecommendedAction.ReadCurrent

    let started =
        rejection
            RecoveryRejectionCode.SubmissionAlreadyStarted
            "The preparation has already started submission and must be resolved exactly."
            RecommendedAction.RecoverExact

    let accepted =
        rejection
            RecoveryRejectionCode.RecoveryActionUnavailable
            "An accepted operation cannot be dismissed. Inspect its retained receipt."
            RecommendedAction.ReadCurrent

    let digestMismatch =
        rejection
            RecoveryRejectionCode.SourceDigestMismatch
            "The supplied digest does not match the exact retained request bytes."
            RecommendedAction.StopAndInvestigate

    let unsupported =
        rejection
            RecoveryRejectionCode.UnsupportedRecoveryArtifact
            "The recovery artifact is unsupported or failed canonical decoding."
            RecommendedAction.CorrectInput

    let installationMismatch =
        rejection
            RecoveryRejectionCode.InstallationMismatch
            "The recovery envelope belongs to a different ClaimCore installation."
            RecommendedAction.StopAndInvestigate

    let validDigest (value: string) =
        not (Object.ReferenceEquals(value, null))
        && value.Length = 64
        && value
           |> Seq.forall (fun character ->
               ('0' <= character && character <= '9') || ('a' <= character && character <= 'f'))

    let importEffect
        (kind: RecoveryArtifactKind)
        (digest: string)
        (canonical: byte array)
        : Result<RecoveryImportPreview, RecoveryRejection> =
        match RequestRecord.decode SemanticContract.current.RequestByteLimit canonical with
        | Error _ -> Error unsupported
        | Ok request ->
            Ok
                {
                    ArtifactKind = kind
                    SourceSha256 = digest
                    DecodedEffect =
                        {
                            OperationId = request.OperationId
                            CaseReference = request.CaseReference
                            Command = Commands.kind request.Command
                            ExpectedVersion = request.ExpectedVersion
                            AuthoredValues = TypedProjection.authoredValues request
                            CanonicalCommandFormat = RecordVersions.CanonicalCommandFormat
                            RequestSha256 = canonical |> SHA256.HashData |> Convert.ToHexStringLower
                        }
                    ExistingPreparation = None
                }

    let preparationDraft
        (kind: RecoveryArtifactKind)
        (effect: RecoveryImportEffect)
        (canonical: byte array)
        : RecoveryPreparationDraft =
        {
            OperationId = effect.OperationId
            CanonicalRequestFormat = effect.CanonicalCommandFormat
            RequestSha256 = effect.RequestSha256
            CanonicalRequest = canonical
            PreparingApplicationVersion = BuildIdentity.current.Version
            PreparingContractFingerprint =
                SemanticContract.fingerprint SemanticContract.current
                |> SemanticCoreFingerprint.value
            PreparingContractKind =
                match kind with
                | RecoveryArtifactKind.Envelope -> PreparingContractKind.SemanticCoreV1
                | RecoveryArtifactKind.UnboundCanonicalRecord ->
                    PreparingContractKind.LegacyUnclassified
        }

    let existing
        (recovery: IRecoveryStore)
        (effect: RecoveryImportEffect)
        (cancellationToken: CancellationToken)
        : Task<RecoveryQueryOutcome<PreparationSummary option>> =
        task {
            match! recovery.Get(effect.OperationId, cancellationToken) with
            | _ when cancellationToken.IsCancellationRequested ->
                return RecoveryQueryOutcome.RecoveryCancelled
            | Error RecoveryStoreFailure.ReadCancelled ->
                return RecoveryQueryOutcome.RecoveryCancelled
            | Error failure ->
                return RecoveryQueryOutcome.RecoveryFailed(TypedProjection.recoveryFault failure)
            | Ok None -> return RecoveryQueryOutcome.RecoverySucceeded None
            | Ok(Some value) ->
                return
                    TypedProjection.summary false value
                    |> Result.map (Some >> RecoveryQueryOutcome.RecoverySucceeded)
                    |> Result.defaultWith RecoveryQueryOutcome.RecoveryFailed
        }

    let attachExisting
        (recovery: IRecoveryStore)
        (preview: RecoveryImportPreview)
        (cancellationToken: CancellationToken)
        : Task<RecoveryQueryOutcome<RecoveryImportPreview>> =
        task {
            match! existing recovery preview.DecodedEffect cancellationToken with
            | RecoveryQueryOutcome.RecoveryCancelled ->
                return RecoveryQueryOutcome.RecoveryCancelled
            | RecoveryQueryOutcome.RecoveryFailed fault ->
                return RecoveryQueryOutcome.RecoveryFailed fault
            | RecoveryQueryOutcome.RecoveryRejected rejection ->
                return RecoveryQueryOutcome.RecoveryRejected rejection
            | RecoveryQueryOutcome.RecoverySucceeded current ->
                return
                    RecoveryQueryOutcome.RecoverySucceeded
                        { preview with
                            ExistingPreparation = current
                        }
        }

    let decodeEnvelope
        (recovery: IRecoveryStore)
        (source: byte array)
        (cancellationToken: CancellationToken)
        : Task<ImportDecoding> =
        task {
            match RecoveryEnvelope.decode SemanticContract.current.RequestByteLimit source with
            | Error _ -> return ImportRefused unsupported
            | Ok envelope ->
                match! recovery.InstallationLineage cancellationToken with
                | _ when cancellationToken.IsCancellationRequested -> return ImportCancelled
                | Error RecoveryStoreFailure.ReadCancelled -> return ImportCancelled
                | Error failure -> return ImportFailed(TypedProjection.recoveryFault failure)
                | Ok lineage when lineage <> envelope.InstallationId ->
                    return ImportRefused installationMismatch
                | Ok _ ->
                    match
                        importEffect
                            RecoveryArtifactKind.Envelope
                            (source |> SHA256.HashData |> Convert.ToHexStringLower)
                            envelope.CanonicalRequest
                    with
                    | Ok value -> return Imported value
                    | Error value -> return ImportRefused value
        }

    let decodeCanonical (source: byte array) : Result<RecoveryImportPreview, RecoveryRejection> =
        importEffect
            RecoveryArtifactKind.UnboundCanonicalRecord
            (source |> SHA256.HashData |> Convert.ToHexStringLower)
            source

    let importedCanonical
        (kind: RecoveryArtifactKind)
        (source: byte array)
        : Result<byte array, RecoveryRejection> =
        match kind with
        | RecoveryArtifactKind.Envelope ->
            match RecoveryEnvelope.decode SemanticContract.current.RequestByteLimit source with
            | Ok envelope -> Ok envelope.CanonicalRequest
            | Error _ -> Error unsupported
        | RecoveryArtifactKind.UnboundCanonicalRecord -> Ok source

    let resolveOutcome (result: RetainedResolution) : ResolveOutcome =
        match result with
        | RetainedResolution.ObservedReceipt receipt ->
            ResolveOutcome.ResolveObservedAccepted receipt
        | RetainedResolution.Resolved(preparation, attemptId, execution, settlement) ->
            ResolveOutcome.ResolveCompleted(preparation, attemptId, execution, settlement)
        | RetainedResolution.MissingPreparation _ ->
            ResolveOutcome.RefusedBeforeAttempt(None, notFound)
        | RetainedResolution.DismissedPreparation preparation ->
            ResolveOutcome.RefusedBeforeAttempt(Some preparation, dismissed)
        | RetainedResolution.DigestConflict preparation ->
            ResolveOutcome.RefusedBeforeAttempt(Some preparation, digestMismatch)
        | RetainedResolution.ReceiptIdentityConflict preparation ->
            ResolveOutcome.RefusedBeforeAttempt(Some preparation, conflict)
        | RetainedResolution.ResolutionCancelledBeforeAdmission operationId ->
            ResolveOutcome.ResolveCancelledBeforeAdmission operationId
        | RetainedResolution.ResolutionFailedBeforeAttempt(preparation, fault) ->
            ResolveOutcome.ResolveFailedBeforeAttempt(preparation, fault)
        | RetainedResolution.ResolutionCancelledBeforeAttempt preparation ->
            ResolveOutcome.ResolveCancelledBeforeAttempt preparation
        | RetainedResolution.ResolutionAdmissionUnknown(preparation, fault) ->
            ResolveOutcome.ResolveAttemptAdmissionUnknown(preparation, fault)
        | RetainedResolution.ResolutionUnresolved(preparation, attemptId, fault) ->
            ResolveOutcome.ResolveAttemptUnresolved(preparation, attemptId, fault)
