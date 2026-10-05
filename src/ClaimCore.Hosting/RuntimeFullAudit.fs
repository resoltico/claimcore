namespace ClaimCore.Hosting

open System
open System.Data
open System.Threading
open Npgsql
open ClaimCore.Postgres

/// The session lease drains complete authority operations, including post-COMMIT settlement,
/// before primary and witness locks establish the complete stable-snapshot audit.
module internal RuntimeFullAudit =
    let runWith
        (resources: RuntimeResources)
        (beforeWitnessFence: unit -> System.Threading.Tasks.Task)
        (afterFence: unit -> System.Threading.Tasks.Task)
        (cancellationToken: CancellationToken)
        =
        task {
            use! barrier = resources.FullAuditDataSource.OpenConnectionAsync(cancellationToken)

            use! _operationFence =
                AuthorityOperationFence.acquireExclusive
                    (Some resources.FullAuditDataSource)
                    barrier
                    cancellationToken

            use! auditConnection =
                RuntimeDatabase.openConnectionAsyncWithCancellation
                    resources.FullAuditDataSource
                    cancellationToken

            use! transaction =
                barrier.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)

            use lockCommand =
                new NpgsqlCommand(
                    "SELECT revision FROM claimcore.authority_tip WHERE singleton FOR UPDATE",
                    barrier,
                    transaction
                )

            let! revision = lockCommand.ExecuteScalarAsync(cancellationToken)

            if not (revision :? int64) then
                invalidOp "Primary audit barrier is unavailable."

            let witness = resources.Witness
            let! initial = witness.Snapshot(cancellationToken)
            let generation = initial.WriterGeneration
            do! beforeWitnessFence ()
            use! _fence = witness.AcquireReadFence(generation, cancellationToken)
            let! before = witness.Snapshot(cancellationToken)
            do! afterFence ()

            let! summary =
                DataAudit.runWithSuppression
                    auditConnection
                    witness
                    (Some resources.Suppression)
                    cancellationToken

            let! after = witness.Snapshot(cancellationToken)

            if
                summary.WitnessCutoff <> before.TipSequence
                || after.TipSequence <> before.TipSequence
            then
                invalidOp "Witness cutoff changed during the complete audit."

            do! transaction.RollbackAsync(cancellationToken)
            return summary
        }

    let run (resources: RuntimeResources) (cancellationToken: CancellationToken) =
        runWith
            resources
            (fun () -> System.Threading.Tasks.Task.CompletedTask)
            (fun () -> System.Threading.Tasks.Task.CompletedTask)
            cancellationToken
