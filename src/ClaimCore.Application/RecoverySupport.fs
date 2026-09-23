namespace ClaimCore.Application

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.RecordFormat

module internal RecoverySupport =
    let revoked = RecoveryRejection.OperationRevoked

    type ImportDecoding =
        | Imported of RecoveryImportPreview
        | ImportRefused of RecoveryRejection
        | ImportFailed of CoreFault
        | ImportCancelled

    let attemptLimit = RecoveryRejection.AttemptLimitReached
    let notFound = RecoveryRejection.PreparationNotFound
    let conflict = RecoveryRejection.ContentConflict
    let dismissed = RecoveryRejection.PreparationDismissed
    let started = RecoveryRejection.SubmissionAlreadyStarted
    let accepted = RecoveryRejection.AcceptedOperationCannotBeDismissed
    let digestMismatch = RecoveryRejection.RequestDigestMismatch
    let installationMismatch = RecoveryRejection.InstallationMismatch

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
        | Error _ -> Error RecoveryRejection.CanonicalRecordInvalidOrUnsupported
        | Ok request when
            not (CryptographicOperations.FixedTimeEquals(RequestRecord.encode request, canonical))
            ->
            Error RecoveryRejection.CanonicalRecordInvalidOrUnsupported
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
                    PreparingContractKind.CanonicalRecordV3
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
            | Ok None
            | Ok(Some(RecoveryStoredOperation.RevokedTombstone _)) ->
                return RecoveryQueryOutcome.RecoverySucceeded None
            | Ok(Some(RecoveryStoredOperation.Retained(value, authority))) ->
                return
                    TypedProjection.summaryWithKnownAuthority false authority value
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
            | Error _ -> return ImportRefused RecoveryRejection.EnvelopeInvalidOrUnsupported
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
            | Error _ -> Error RecoveryRejection.EnvelopeInvalidOrUnsupported
        | RecoveryArtifactKind.UnboundCanonicalRecord -> Ok source

    let private completedOutcome =
        function
        | RetainedResolution.ObservedReceipt receipt ->
            Some(ResolveOutcome.ResolveObservedAccepted receipt)
        | RetainedResolution.Resolved(preparation, attemptId, execution, settlement) ->
            Some(ResolveOutcome.ResolveCompleted(preparation, attemptId, execution, settlement))
        | _ -> None

    let private refusedOutcome =
        function
        | RetainedResolution.MissingPreparation _ ->
            Some(ResolveOutcome.RefusedBeforeAttempt(None, notFound))
        | RetainedResolution.DismissedPreparation preparation ->
            Some(ResolveOutcome.RefusedBeforeAttempt(Some preparation, dismissed))
        | RetainedResolution.RevokedPreparation preparation ->
            Some(ResolveOutcome.RefusedBeforeAttempt(preparation, revoked))
        | RetainedResolution.AttemptLimitReached preparation ->
            Some(ResolveOutcome.RefusedBeforeAttempt(Some preparation, attemptLimit))
        | RetainedResolution.DigestConflict ->
            Some(ResolveOutcome.RefusedBeforeAttempt(None, digestMismatch))
        | RetainedResolution.ReceiptIdentityConflict ->
            Some(ResolveOutcome.RefusedBeforeAttempt(None, conflict))
        | _ -> None

    let private interruptedOutcome =
        function
        | RetainedResolution.ResolutionCancelledBeforeAdmission operationId ->
            Some(ResolveOutcome.ResolveCancelledBeforeAdmission operationId)
        | RetainedResolution.ResolutionFailedBeforeAttempt(preparation, fault) ->
            Some(ResolveOutcome.ResolveFailedBeforeAttempt(preparation, fault))
        | RetainedResolution.ResolutionCancelledBeforeAttempt preparation ->
            Some(ResolveOutcome.ResolveCancelledBeforeAttempt preparation)
        | RetainedResolution.ResolutionAdmissionUnknown(preparation, fault) ->
            Some(ResolveOutcome.ResolveAttemptAdmissionUnknown(preparation, fault))
        | RetainedResolution.ResolutionUnresolved(preparation, attemptId, fault) ->
            Some(ResolveOutcome.ResolveAttemptUnresolved(preparation, attemptId, fault))
        | _ -> None

    let resolveOutcome (result: RetainedResolution) : ResolveOutcome =
        completedOutcome result
        |> Option.orElseWith (fun () -> refusedOutcome result)
        |> Option.orElseWith (fun () -> interruptedOutcome result)
        |> Option.defaultWith (fun () ->
            invalidOp "An unsupported recovery resolution was returned.")
