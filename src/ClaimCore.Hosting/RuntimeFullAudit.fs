namespace ClaimCore.Hosting

open System
open System.Data
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Postgres

/// The session lease drains complete authority operations, including post-COMMIT settlement,
/// before primary and witness locks establish the complete stable-snapshot audit.
module internal RuntimeFullAudit =
    let private witnessFenced
        (resources: RuntimeResources)
        auditConnection
        (hooks: (unit -> Task) * (unit -> Task) * (exn -> unit))
        (ct: CancellationToken)
        =
        task {
            let beforeWitnessFence, afterFence, onFailure = hooks
            let witness = resources.Witness
            let! initial = witness.Snapshot(ct)
            do! beforeWitnessFence ()
            use! _fence = witness.AcquireReadFence(initial.WriterGeneration, ct)

            try
                let! before = witness.Snapshot(ct)
                do! afterFence ()

                let! summary =
                    DataAudit.runWithSuppression
                        auditConnection
                        witness
                        (Some resources.Suppression)
                        ct

                let! after = witness.Snapshot(ct)

                if
                    summary.WitnessCutoff <> before.TipSequence
                    || after.TipSequence <> before.TipSequence
                then
                    invalidOp "Witness cutoff changed during the complete audit."

                return summary
            with error ->
                onFailure error
                return raise error
        }

    let private primaryFenced
        resources
        (barrier: NpgsqlConnection)
        auditConnection
        (hooks: (unit -> Task) * (unit -> Task) * (exn -> unit))
        (ct: CancellationToken)
        =
        task {
            let _, _, onFailure = hooks
            use! transaction = barrier.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)

            try
                use command =
                    new NpgsqlCommand(
                        "SELECT revision FROM claimcore.authority_tip WHERE singleton FOR UPDATE",
                        barrier,
                        transaction
                    )

                let! revision = command.ExecuteScalarAsync(ct)

                if not (revision :? int64) then
                    invalidOp "Primary audit barrier is unavailable."

                let! summary = witnessFenced resources auditConnection hooks ct
                do! transaction.RollbackAsync(ct)
                return summary
            with error ->
                onFailure error
                return raise error
        }

    let runGuardedWith
        (resources: RuntimeResources)
        (beforeWitnessFence: unit -> Task)
        (afterFence: unit -> Task)
        (onFailure: exn -> unit)
        (ct: CancellationToken)
        =
        task {
            use! barrier = resources.FullAuditDataSource.OpenConnectionAsync(ct)

            use! _lease =
                AuthorityOperationFence.acquireExclusiveWithin
                    (Some resources.FullAuditDataSource)
                    barrier
                    7200
                    ct

            try
                use! audit =
                    RuntimeDatabase.openConnectionAsyncWithCancellation
                        resources.FullAuditDataSource
                        ct

                return!
                    primaryFenced
                        resources
                        barrier
                        audit
                        (beforeWitnessFence, afterFence, onFailure)
                        ct
            with error ->
                onFailure error
                return raise error
        }

    let runWith resources beforeWitnessFence afterFence ct =
        runGuardedWith resources beforeWitnessFence afterFence (fun _ -> ()) ct

    let runScheduled resources onFailure ct =
        runGuardedWith
            resources
            (fun () -> Task.CompletedTask)
            (fun () -> Task.CompletedTask)
            onFailure
            ct

    let run (resources: RuntimeResources) (ct: CancellationToken) =
        runWith resources (fun () -> Task.CompletedTask) (fun () -> Task.CompletedTask) ct
