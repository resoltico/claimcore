namespace ClaimCore.Application

open System.Threading

/// Public recovery capability assembled from focused Application-owned workflows. PostgreSQL only
/// supplies `IRecoveryStore`; neither adapters nor Runtime expose that technical port.
type internal RecoveryWorkflow(store: IClaimStore, recovery: IRecoveryStore, clock: IBusinessDate) =
    interface IRecoveryWorkflow with
        member _.List(afterCursor, limit, cancellationToken) =
            RecoveryReadOperations.list recovery afterCursor limit cancellationToken

        member _.Inspect(operationId, cancellationToken) =
            RecoveryReadOperations.inspect store recovery operationId cancellationToken

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
            RecoveryReadOperations.export recovery operationId requestSha256 cancellationToken

        member _.PreviewEnvelopeImport(source, cancellationToken) =
            RecoveryImports.previewEnvelope recovery source cancellationToken

        member _.RetainEnvelopeImport(source, sourceSha256, cancellationToken) =
            RecoveryImports.retainEnvelope recovery source sourceSha256 cancellationToken

        member _.PreviewCanonicalRecordImport(source, cancellationToken) =
            RecoveryImports.previewCanonical recovery source cancellationToken

        member _.RetainCanonicalRecordImport(source, sourceSha256, cancellationToken) =
            RecoveryImports.retainCanonical recovery source sourceSha256 cancellationToken

module internal RecoveryCoordinator =
    let create store recovery clock : IRecoveryWorkflow =
        new RecoveryWorkflow(store, recovery, clock) :> IRecoveryWorkflow
