namespace ClaimCore.Application

open System.Threading

/// Public recovery capability assembled from focused Application-owned workflows. PostgreSQL only
/// supplies `IRecoveryStore`; neither adapters nor Runtime expose that technical port.
type internal RecoveryWorkflow
    (
        store: IClaimStore,
        recovery: IRecoveryStore,
        clock: IBusinessTime,
        importer: ActorBinding,
        artifactAuthority: IRecoveryArtifactAuthority
    ) =
    interface IRecoveryWorkflow with
        member _.List(view, afterCursor, limit, cancellationToken) =
            RecoveryReadOperations.list recovery view afterCursor limit cancellationToken

        member _.Inspect(operationId, afterCursor, limit, cancellationToken) =
            RecoveryReadOperations.inspect
                store
                recovery
                operationId
                afterCursor
                limit
                cancellationToken

        member _.Resolve(operationId, requestSha256, cancellationToken) =
            RecoveryReadOperations.resolve
                store
                recovery
                clock
                operationId
                requestSha256
                cancellationToken

        member _.Dismiss(operationId, requestSha256, confirmed, cancellationToken) =
            RecoveryDismissOperations.dismiss
                store
                recovery
                operationId
                requestSha256
                confirmed
                cancellationToken

        member _.ExportEnvelope(operationId, requestSha256, cancellationToken) =
            RecoveryExports.export
                recovery
                artifactAuthority
                operationId
                requestSha256
                cancellationToken

        member _.PreviewEnvelopeImport(source, cancellationToken) =
            RecoveryImports.previewEnvelope artifactAuthority source cancellationToken

        member _.RetainEnvelopeImport(source, sourceSha256, cancellationToken) =
            RecoveryImports.retainEnvelope
                recovery
                artifactAuthority
                importer
                source
                sourceSha256
                cancellationToken

module internal RecoveryCoordinator =
    let create store recovery clock importer artifactAuthority : IRecoveryWorkflow =
        new RecoveryWorkflow(store, recovery, clock, importer, artifactAuthority)
        :> IRecoveryWorkflow
