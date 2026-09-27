namespace ClaimCore.Hosting

open System
open System.Data
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.RecordFormat

/// Key material is loaded from owner-private custody for one call and erased on completion.
/// Runtime composes this only after the managed-copy and full-audit qualifications pass.
module internal RecoveryArtifactAuthority =
    let private decoded (ring: RecoveryArtifactKeyRing) (witness: WitnessProtocol) now source =
        let resolved keyId =
            ring.Resolve(keyId, now) |> Option.map (fun key -> key.Encryption, key.Mac)

        RecoveryEnvelopeV3.decode
            65536
            resolved
            witness.Identity.InstallationId
            witness.Identity.Epoch
            now
            ring.Lifetime
            source

    let private expectedExportId
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        operationId
        =
        RecoveryArtifactExportCandidate.exportId
            witness.Identity.InstallationId
            witness.Identity.Epoch
            operationId
            context.Binding.ActorId
            context.Binding.GrantRevision

    let private verifyExisting
        (ring: RecoveryArtifactKeyRing)
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (retained: RetainedPreparation)
        now
        (source: byte array)
        =
        let resolved keyId =
            ring.Resolve(keyId, now) |> Option.map (fun key -> key.Encryption, key.Mac)

        match
            RecoveryEnvelopeV3.decode
                65536
                resolved
                witness.Identity.InstallationId
                witness.Identity.Epoch
                now
                ring.Lifetime
                source
        with
        | Error _ -> false
        | Ok(artifact: RecoveryArtifactV3) ->
            try
                artifact.ExportId = expectedExportId witness context retained.OperationId
                && artifact.OperationId = retained.OperationId
                && artifact.CaseId = retained.CaseId
                && artifact.PreparerActorId = retained.PreparerActorId
                && artifact.PreparerGrantRevision = retained.PreparerGrantRevision
                && artifact.ImporterActorId = retained.ImporterActorId
                && artifact.ExporterActorId = context.Binding.ActorId
                && artifact.ExporterGrantRevision = context.Binding.GrantRevision
                && CryptographicOperations.FixedTimeEquals(
                    ReadOnlySpan<byte>(artifact.CanonicalRequest),
                    ReadOnlySpan<byte>(retained.CanonicalRequest)
                )
            finally
                CryptographicOperations.ZeroMemory(artifact.CanonicalRequest)

    let sign
        (source: NpgsqlDataSource)
        (witness: WitnessProtocol)
        (clock: IBusinessTime)
        (context: ActorCallContext)
        path
        (retained: RetainedPreparation)
        (ct: CancellationToken)
        : Task<Result<byte array, CoreFault>> =
        task {
            if String.IsNullOrWhiteSpace path then
                return Error CoreFault.RecoveryStoreUnavailable
            else
                try
                    use ring = RecoveryArtifactKeyCustody.load path
                    let instant = (clock.Capture()).ObservedUtcInstant

                    match ring.Active instant with
                    | None -> return Error CoreFault.RecoveryStoreUnavailable
                    | Some key ->
                        let issueKey: RecoveryArtifactIssueKey =
                            {
                                Id = key.Id
                                IssueFrom = key.IssueFrom
                                IssueUntil = key.IssueUntil
                                Lifetime = ring.Lifetime
                                MaximumExports = key.MaximumExports
                            }

                        return!
                            RecoveryArtifactExportIssue.issue
                                source
                                witness
                                context
                                retained
                                issueKey
                                (fun () -> (clock.Capture()).ObservedUtcInstant)
                                (RecoveryEnvelopeV3.encode 65536 key.Encryption key.Mac)
                                (verifyExisting ring witness context retained instant)
                                ct
                with
                | :? OperationCanceledException when ct.IsCancellationRequested ->
                    return Error CoreFault.RecoveryMutationCancelled
                | _ -> return Error CoreFault.RecoveryStoreUnavailable
        }

    let private verifiedArtifact (artifact: RecoveryArtifactV3) : VerifiedRecoveryArtifact =
        {
            OperationId = artifact.OperationId
            CaseId = artifact.CaseId
            PreparerActorId = artifact.PreparerActorId
            PreparerGrantRevision = artifact.PreparerGrantRevision
            CanonicalRequest = artifact.CanonicalRequest
        }

    let private proveArtifact source witness context bytes ct (artifact: RecoveryArtifactV3) =
        task {
            try
                let expected =
                    RecoveryArtifactExportCandidate.exportId
                        artifact.InstallationId
                        artifact.Epoch
                        artifact.OperationId
                        artifact.ExporterActorId
                        artifact.ExporterGrantRevision

                let! permitted =
                    if expected <> artifact.ExportId then
                        Task.FromResult false
                    else
                        RecoveryArtifactImportProof.verify source witness context artifact bytes ct

                if permitted then
                    return Ok(verifiedArtifact artifact)
                else
                    CryptographicOperations.ZeroMemory(artifact.CanonicalRequest)
                    return Error RecoveryRejection.ResourceUnavailable
            with _ ->
                CryptographicOperations.ZeroMemory(artifact.CanonicalRequest)
                return Error RecoveryRejection.ResourceUnavailable
        }

    let verify
        (source: NpgsqlDataSource)
        (witness: WitnessProtocol)
        (clock: IBusinessTime)
        (context: ActorCallContext)
        path
        (bytes: byte array)
        (ct: CancellationToken)
        : Task<Result<VerifiedRecoveryArtifact, RecoveryRejection>> =
        task {
            if
                context.Action <> EndpointAction.RecoveryImportPreview
                && context.Action <> EndpointAction.RecoveryImportRetain
            then
                return Error RecoveryRejection.ResourceUnavailable
            elif String.IsNullOrWhiteSpace path then
                return Error RecoveryRejection.ResourceUnavailable
            elif
                Object.ReferenceEquals(box bytes, null)
                || bytes.Length = 0
                || int64 bytes.Length > int64 SemanticContract.current.RequestByteLimit * 3L + 4096L
            then
                return Error RecoveryRejection.EnvelopeInvalidOrUnsupported
            else
                try
                    use ring = RecoveryArtifactKeyCustody.load path
                    let instant = (clock.Capture()).ObservedUtcInstant

                    match decoded ring witness instant bytes with
                    | Error _ -> return Error RecoveryRejection.EnvelopeInvalidOrUnsupported
                    | Ok artifact -> return! proveArtifact source witness context bytes ct artifact
                with
                | :? OperationCanceledException when ct.IsCancellationRequested ->
                    return Error RecoveryRejection.ResourceUnavailable
                | _ -> return Error RecoveryRejection.ResourceUnavailable
        }
