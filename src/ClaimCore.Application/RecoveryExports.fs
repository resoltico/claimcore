namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.RecordFormat

/// Recovery export remains a separate narrow read because it handles claimant-bearing artifact bytes
/// while ordinary recovery list and inspection stay metadata-only.
module internal RecoveryExports =
    let private encode
        (operationId: Guid)
        (lineage: Guid)
        (retained: RetainedPreparation)
        : Result<RecoveryExport, CoreFault> =
        match
            RequestRecord.decode SemanticContract.current.RequestByteLimit retained.CanonicalRequest
        with
        | Error _ -> Error(CoreFault.RetainedCanonicalInvalid)
        | Ok _ ->
            let bytes =
                RecoveryEnvelope.encode
                    {
                        InstallationId = lineage
                        OperationId = retained.OperationId
                        ProtocolVersion = retained.CanonicalRequestFormat
                        RequestFingerprintVersion = RecordVersions.RequestFingerprint
                        RequestSha256 = retained.RequestSha256
                        CanonicalRequest = retained.CanonicalRequest
                    }

            Ok
                {
                    Bytes = bytes
                    FileName = "claimcore-recovery-" + operationId.ToString("D") + ".json"
                    MediaType = "application/vnd.claimcore.recovery+json"
                    RequestSha256 = retained.RequestSha256
                }

    let private exportRetained
        (recovery: IRecoveryStore)
        (operationId: Guid)
        (cancellationToken: CancellationToken)
        (retained: RetainedPreparation)
        : Task<RecoveryQueryOutcome<Lookup<RecoveryExport, Guid>>> =
        task {
            match! recovery.InstallationLineage cancellationToken with
            | Error RecoveryStoreFailure.ReadCancelled ->
                return RecoveryQueryOutcome.RecoveryCancelled
            | Error failure ->
                return RecoveryQueryOutcome.RecoveryFailed(TypedProjection.recoveryFault failure)
            | Ok lineage ->
                match encode operationId lineage retained with
                | Error fault -> return RecoveryQueryOutcome.RecoveryFailed fault
                | Ok _ when cancellationToken.IsCancellationRequested ->
                    return RecoveryQueryOutcome.RecoveryCancelled
                | Ok artifact ->
                    return RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found artifact)
        }

    let export
        (recovery: IRecoveryStore)
        (operationId: Guid)
        (requestSha256: string)
        (cancellationToken: CancellationToken)
        : Task<RecoveryQueryOutcome<Lookup<RecoveryExport, Guid>>> =
        if cancellationToken.IsCancellationRequested then
            Task.FromResult(RecoveryQueryOutcome.RecoveryCancelled)
        elif operationId = Guid.Empty then
            Task.FromResult(
                RecoveryQueryOutcome.RecoveryRejected RecoveryRejection.OperationIdRequired
            )
        elif not (RecoverySupport.validDigest requestSha256) then
            Task.FromResult(
                RecoveryQueryOutcome.RecoveryRejected RecoveryRejection.RequestDigestInvalid
            )
        else
            task {
                match! recovery.Get(operationId, cancellationToken) with
                | Error RecoveryStoreFailure.ReadCancelled ->
                    return RecoveryQueryOutcome.RecoveryCancelled
                | Error failure ->
                    return
                        RecoveryQueryOutcome.RecoveryFailed(TypedProjection.recoveryFault failure)
                | Ok None ->
                    return RecoveryQueryOutcome.RecoverySucceeded(Lookup.NotFound operationId)
                | Ok(Some(RecoveryStoredOperation.RevokedTombstone revocation)) when
                    revocation.RequestSha256 = requestSha256
                    ->
                    return RecoveryQueryOutcome.RecoveryRejected RecoverySupport.revoked
                | Ok(Some(RecoveryStoredOperation.RevokedTombstone _)) ->
                    return RecoveryQueryOutcome.RecoveryRejected RecoverySupport.conflict
                | Ok(Some(RecoveryStoredOperation.Retained(retained, _))) when
                    retained.RequestSha256 <> requestSha256
                    ->
                    return RecoveryQueryOutcome.RecoveryRejected RecoverySupport.conflict
                | Ok(Some(RecoveryStoredOperation.Retained(_, _))) when
                    cancellationToken.IsCancellationRequested
                    ->
                    return RecoveryQueryOutcome.RecoveryCancelled
                | Ok(Some(RecoveryStoredOperation.Retained(retained, _))) ->
                    return! exportRetained recovery operationId cancellationToken retained
            }
