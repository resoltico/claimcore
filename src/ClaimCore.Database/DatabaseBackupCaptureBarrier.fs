namespace ClaimCore.Database

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres

[<NoEquality; NoComparison>]
type internal BackupCaptureCutoff =
    {
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        WriterGeneration: int64
        WitnessSequence: int64
        WitnessHash: byte array
        AuthorityEvents: int64
        AcceptedOperations: int64
    }

module private DatabaseBackupCaptureAdmission =
    let auditedCutoff
        (dataSource: NpgsqlDataSource)
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments)
        expectedGeneration
        (cancellationToken: CancellationToken)
        =
        task {
            let held = witness.Snapshot()

            if
                held.HandoffPending
                || held.ActivationPending
                || held.WriterGeneration <> expectedGeneration
            then
                invalidOp "Writer generation changed before backup capture."

            use! connection = RuntimeDatabase.openConnectionAsync dataSource

            let! summary =
                DataAudit.runWithSuppression connection witness (Some commitments) cancellationToken

            if summary.PendingIntents <> 0L || summary.WitnessCutoff <> held.TipSequence then
                invalidOp "Backup capture cannot begin with unsettled authority."

            return
                {
                    InstallationId = witness.Identity.InstallationId
                    LineageId = witness.Identity.LineageId
                    Epoch = witness.Identity.Epoch
                    WriterGeneration = held.WriterGeneration
                    WitnessSequence = held.TipSequence
                    WitnessHash = Array.copy held.TipHash
                    AuthorityEvents = summary.AuthorityEvents
                    AcceptedOperations = summary.AcceptedOperations
                }
        }

/// A single witness FOR SHARE lease freezes authority while complete primary/witness audits and
/// both physical BASE streams run. It does not itself register, retain, or certify a backup.
[<Sealed>]
type internal DatabaseBackupCaptureBarrier
    private
    (
        dataSource: NpgsqlDataSource,
        witness: WitnessProtocol,
        commitments: ISuppressionCommitments,
        lease: IDisposable,
        cutoff: BackupCaptureCutoff
    ) =
    let mutable disposed = false

    member _.Cutoff =
        { cutoff with
            WitnessHash = Array.copy cutoff.WitnessHash
        }

    member _.Verify(cancellationToken: CancellationToken) =
        task {
            if disposed then
                invalidOp "Backup capture authority fence is closed."

            let before = witness.Snapshot()

            if
                before.WriterGeneration <> cutoff.WriterGeneration
                || before.TipSequence <> cutoff.WitnessSequence
                || not (CryptographicOperations.FixedTimeEquals(before.TipHash, cutoff.WitnessHash))
                || before.HandoffPending
                || before.ActivationPending
            then
                invalidOp "Backup capture authority cutoff changed."

            use! connection = RuntimeDatabase.openConnectionAsync dataSource

            let! summary =
                DataAudit.runWithSuppression connection witness (Some commitments) cancellationToken

            let after = witness.Snapshot()

            if
                summary.PendingIntents <> 0L
                || summary.WitnessCutoff <> cutoff.WitnessSequence
                || summary.AuthorityEvents <> cutoff.AuthorityEvents
                || summary.AcceptedOperations <> cutoff.AcceptedOperations
                || after.TipSequence <> cutoff.WitnessSequence
                || not (CryptographicOperations.FixedTimeEquals(after.TipHash, cutoff.WitnessHash))
            then
                invalidOp "Backup capture audit is not quiescent."

            return summary
        }

    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed <- true
                lease.Dispose()

    static member Acquire
        (
            owner: NpgsqlConnection,
            dataSource: NpgsqlDataSource,
            witness: WitnessProtocol,
            commitments: ISuppressionCommitments,
            cancellationToken: CancellationToken
        ) =
        task {
            OwnerConnection.requireIdentity owner
            SchemaBaseline.requireCurrent owner
            witness.Admit()
            let before = witness.Snapshot()

            if before.HandoffPending || before.ActivationPending then
                invalidOp "Writer authority is not active for backup capture."

            let lease = witness.AcquireReadFence(before.WriterGeneration)

            try
                let! cutoff =
                    DatabaseBackupCaptureAdmission.auditedCutoff
                        dataSource
                        witness
                        commitments
                        before.WriterGeneration
                        cancellationToken

                return
                    new DatabaseBackupCaptureBarrier(
                        dataSource,
                        witness,
                        commitments,
                        lease,
                        cutoff
                    )
            with error ->
                lease.Dispose()
                return raise error
        }
