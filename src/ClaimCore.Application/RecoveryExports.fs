namespace ClaimCore.Application

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open ClaimCore.RecordFormat

/// Export discloses claimant-bearing bytes and issues witnessed custody evidence. A completed
/// issuance is not relabelled as cancellation; ordinary recovery reads stay metadata-only.
module internal RecoveryExports =
    let private encode
        (artifactAuthority: IRecoveryArtifactAuthority)
        (operationId: Guid)
        (retained: RetainedPreparation)
        (cancellationToken: CancellationToken)
        : Task<Result<RecoveryExport, CoreFault>> =
        task {
            match
                RequestRecord.decode
                    SemanticContract.current.RequestByteLimit
                    retained.CanonicalRequest
            with
            | Error _ -> return Error(CoreFault.RetainedCanonicalInvalid)
            | Ok request when
                request.OperationId <> retained.OperationId
                || not (
                    CryptographicOperations.FixedTimeEquals(
                        RequestRecord.encode request,
                        retained.CanonicalRequest
                    )
                )
                ->
                return Error(CoreFault.RetainedCanonicalInvalid)
            | Ok _ ->
                let! signed = artifactAuthority.Sign(retained, cancellationToken)

                return
                    signed
                    |> Result.map (fun bytes ->
                        {
                            Bytes = bytes
                            FileName = "claimcore-recovery-" + operationId.ToString("D") + ".json"
                            MediaType = "application/vnd.claimcore.recovery+json"
                            RequestSha256 = retained.RequestSha256
                        })
        }

    let private exportRetained
        (artifactAuthority: IRecoveryArtifactAuthority)
        (operationId: Guid)
        (cancellationToken: CancellationToken)
        (retained: RetainedPreparation)
        : Task<RecoveryQueryOutcome<Lookup<RecoveryExport, Guid>>> =
        task {
            match! encode artifactAuthority operationId retained cancellationToken with
            | Error fault -> return RecoveryQueryOutcome.RecoveryFailed fault
            | Ok artifact -> return RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found artifact)
        }

    let private fromStored
        artifactAuthority
        operationId
        requestSha256
        (cancellationToken: CancellationToken)
        (stored: RecoveryStoredOperation option)
        =
        task {
            match stored with
            | None -> return RecoveryQueryOutcome.RecoverySucceeded(Lookup.NotFound operationId)
            | Some(RecoveryStoredOperation.RevokedTombstone revocation) when
                revocation.RequestSha256 = requestSha256
                ->
                return RecoveryQueryOutcome.RecoveryRejected RecoverySupport.revoked
            | Some(RecoveryStoredOperation.RevokedTombstone _) ->
                return RecoveryQueryOutcome.RecoveryRejected RecoverySupport.conflict
            | Some(RecoveryStoredOperation.Retained(retained, _)) when
                retained.RequestSha256 <> requestSha256
                ->
                return RecoveryQueryOutcome.RecoveryRejected RecoverySupport.conflict
            | Some(RecoveryStoredOperation.Retained(_, RecoveryAuthority.RevokedAuthority)) ->
                return RecoveryQueryOutcome.RecoveryRejected RecoverySupport.revoked
            | Some(RecoveryStoredOperation.Retained(_, _)) when
                cancellationToken.IsCancellationRequested
                ->
                return RecoveryQueryOutcome.RecoveryCancelled
            | Some(RecoveryStoredOperation.Retained(retained, _)) ->
                return! exportRetained artifactAuthority operationId cancellationToken retained
        }

    let export
        (recovery: IRecoveryStore)
        (artifactAuthority: IRecoveryArtifactAuthority)
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
                | Error RecoveryStoreFailure.ResourceUnavailable ->
                    return
                        RecoveryQueryOutcome.RecoveryRejected RecoveryRejection.ResourceUnavailable
                | Error failure ->
                    return
                        RecoveryQueryOutcome.RecoveryFailed(TypedProjection.recoveryFault failure)
                | Ok stored ->
                    return!
                        fromStored
                            artifactAuthority
                            operationId
                            requestSha256
                            cancellationToken
                            stored
            }
