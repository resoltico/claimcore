namespace ClaimCore.Hosting

open System
open System.Data
open System.Threading
open Npgsql
open ClaimCore.Postgres

/// The primary authority lock drains admitted mutations before the witness read fence
/// blocks further tickets. Both remain held through the complete stable-snapshot audit.
module internal RuntimeFullAudit =
    let runWith
        (resources: RuntimeResources)
        (afterFence: unit -> System.Threading.Tasks.Task)
        (cancellationToken: CancellationToken)
        =
        task {
            use! barrier = resources.DataSource.OpenConnectionAsync(cancellationToken)

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
            let before = witness.Snapshot()
            use _fence = witness.AcquireReadFence(before.WriterGeneration)
            do! afterFence ()
            use! auditConnection = resources.DataSource.OpenConnectionAsync(cancellationToken)

            let! summary =
                DataAudit.runWithSuppression
                    auditConnection
                    witness
                    (Some resources.Suppression)
                    cancellationToken

            let after = witness.Snapshot()

            if
                summary.WitnessCutoff <> before.TipSequence
                || after.TipSequence <> before.TipSequence
            then
                invalidOp "Witness cutoff changed during the complete audit."

            do! transaction.RollbackAsync(cancellationToken)
            return summary
        }

    let run (resources: RuntimeResources) (cancellationToken: CancellationToken) =
        runWith resources (fun () -> System.Threading.Tasks.Task.CompletedTask) cancellationToken
