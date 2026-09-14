namespace ClaimCore.Postgres

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application

/// PostgreSQL implementation of Application's private technical recovery port. The composed
/// runtime owns the shared data source; focused collaborators own reads, retention, dismissal,
/// and the combined execution transaction.
type internal PostgresRecoveryStore(dataSource: NpgsqlDataSource, limits: PreparationLimits) =
    do PreparationLimits.validate limits

    interface IRecoveryStore with
        member _.InstallationLineage cancellationToken =
            RecoveryStoreQueries.installationLineage dataSource cancellationToken

        member _.Retain(draft, cancellationToken) =
            RecoveryRetentionStore.retain dataSource limits draft cancellationToken

        member _.Get(operationId, cancellationToken) =
            RecoveryReadStore.get dataSource operationId cancellationToken

        member _.Inspect(operationId, after, pageSize, cancellationToken) =
            RecoveryReadStore.inspect dataSource limits operationId after pageSize cancellationToken

        member _.List(view, after, pageSize, cancellationToken) =
            RecoveryReadStore.list dataSource limits view after pageSize cancellationToken

        member _.Start(operationId, cancellationToken) =
            RecoveryRetentionStore.start dataSource limits operationId cancellationToken

        member _.Settle(attemptId, outcome, cancellationToken) =
            RecoveryRetentionStore.settle dataSource attemptId outcome cancellationToken

        member _.ExecuteAdmitted(operation, attemptId, today, decide, cancellationToken) =
            if attemptId = Guid.Empty then
                Task.FromResult(Error(RecoveryStoreFailure.InvalidInput "attemptId"))
            else
                RecoveryExecutionStore.executeAdmitted
                    dataSource
                    operation
                    attemptId
                    today
                    decide
                    cancellationToken

        member _.Dismiss(operationId, requestSha256, cancellationToken) =
            RecoveryDismissalStore.dismiss dataSource operationId requestSha256 cancellationToken
