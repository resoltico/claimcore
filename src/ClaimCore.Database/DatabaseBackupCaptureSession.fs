namespace ClaimCore.Database

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open ClaimCore.HostSecurity
open ClaimCore.Postgres

module private BackupCaptureSessionChecks =
    let sameCutoff (left: BackupCaptureCutoff) (right: BackupCaptureCutoff) =
        left.InstallationId = right.InstallationId
        && left.LineageId = right.LineageId
        && left.Epoch = right.Epoch
        && left.WriterGeneration = right.WriterGeneration
        && left.WitnessSequence = right.WitnessSequence
        && left.AuthorityEvents = right.AuthorityEvents
        && left.AcceptedOperations = right.AcceptedOperations
        && left.WitnessHash.Length = 32
        && right.WitnessHash.Length = 32
        && CryptographicOperations.FixedTimeEquals(left.WitnessHash, right.WitnessHash)

    let digest (value: string) =
        value.Length = 64
        && (value |> Seq.forall (fun c -> ('0' <= c && c <= '9') || ('a' <= c && c <= 'f')))

    let held (value: BackupCaptureHeld) (cutoff: BackupCaptureCutoff) now expires =
        value.CheckedAt = now
        && value.ValidUntil > now
        && value.ValidUntil <= expires
        && sameCutoff value.Cutoff cutoff
        && digest value.MaintenanceEvidenceSha256
        && Path.IsPathFullyQualified value.CycleRoot
        && (match PrivateFileService.requirePrivateDirectory value.CycleRoot with
            | Ok() -> true
            | Error _ -> false)

/// Code-owned physical/custody verifier; test assemblies may inject a synthetic implementation.
type internal IBackupCaptureEvidence =
    abstract Describe:
        cutoff: BackupCaptureCutoff *
        nonce: string *
        leaseId: Guid *
        checkedAt: DateTimeOffset *
        expiresAt: DateTimeOffset ->
            BackupCaptureHeld option

    abstract Seal:
        held: BackupCaptureHeld * files: BackupCaptureFiles * cancellationToken: CancellationToken ->
            Task<BackupCaptureReceipt option>

    abstract Observe:
        receipt: BackupCaptureReceipt * cancellationToken: CancellationToken -> Task<bool>

module private BackupCaptureSessionStart =
    let describe
        (evidence: IBackupCaptureEvidence)
        (barrier: DatabaseBackupCaptureBarrier)
        nonce
        leaseId
        now
        expiresAt
        =
        match evidence.Describe(barrier.Cutoff, nonce, leaseId, now, expiresAt) with
        | Some held when
            held.Nonce = nonce
            && held.LeaseId = leaseId
            && BackupCaptureSessionChecks.held held barrier.Cutoff now expiresAt
            ->
            held
        | _ -> invalidOp "Backup capture lease cannot be described."

/// State transition is private; a FINISH never means that a BASE copy is RETAINED.
[<Sealed>]
type internal DatabaseBackupCaptureSession
    private
    (
        held: BackupCaptureHeld,
        barrier: DatabaseBackupCaptureBarrier,
        evidence: IBackupCaptureEvidence
    ) =
    let mutable state = 0
    let mutable receipt: BackupCaptureReceipt option = None
    member _.ExpiresAt = held.ValidUntil

    member private _.Abort(nonce, leaseId) =
        if nonce <> held.Nonce || leaseId <> held.LeaseId then
            invalidOp "Backup capture abort identity diverged."

        state <- 2
        (barrier :> IDisposable).Dispose()
        DatabaseBackupCaptureResponses.aborted ()

    member private _.Finish
        (nonce, leaseId, files, now: DateTimeOffset, cancellationToken: CancellationToken)
        =
        task {
            if nonce <> held.Nonce || leaseId <> held.LeaseId || now >= held.ValidUntil then
                invalidOp "Backup capture finish identity or time diverged."

            let! _ = barrier.Verify cancellationToken
            let! confirmed = evidence.Seal(held, files, cancellationToken)

            match confirmed with
            | None ->
                return
                    raise (
                        InvalidOperationException("Backup capture files lack owner verification.")
                    )
            | Some value when
                value.Nonce = held.Nonce
                && value.LeaseId = held.LeaseId
                && value.CycleReceiptId <> Guid.Empty
                && value.WitnessSequence = held.Cutoff.WitnessSequence
                && value.WitnessHash = Convert.ToHexStringLower held.Cutoff.WitnessHash
                && BackupCaptureSessionChecks.digest value.WitnessHash
                && BackupCaptureSessionChecks.digest value.ReceiptSha256
                ->
                receipt <- Some value
                state <- 1
                (barrier :> IDisposable).Dispose()
                return DatabaseBackupCaptureResponses.receipt "SEALED" value
            | Some _ ->
                return
                    raise (InvalidOperationException("Backup capture receipt identity diverged."))
        }

    member private _.Observe
        (nonce, cycleReceiptId, receiptSha256, cancellationToken: CancellationToken)
        =
        task {
            match receipt with
            | Some value when
                nonce = value.Nonce
                && cycleReceiptId = value.CycleReceiptId
                && receiptSha256 = value.ReceiptSha256
                ->
                let! accepted = evidence.Observe(value, cancellationToken)

                if not accepted then
                    invalidOp "Backup capture receipt readback diverged."

                state <- 2
                return DatabaseBackupCaptureResponses.receipt "OBSERVED" value
            | _ ->
                return raise (InvalidOperationException("Backup capture receipt is unavailable."))
        }

    member this.Accept
        (frame: byte array, now: DateTimeOffset, cancellationToken: CancellationToken)
        =
        task {
            match state, DatabaseBackupCaptureFrames.parse frame now with
            | 0, BackupCaptureFrame.Abort(nonce, leaseId) -> return this.Abort(nonce, leaseId)
            | 0, BackupCaptureFrame.Finish(nonce, leaseId, files) ->
                return! this.Finish(nonce, leaseId, files, now, cancellationToken)
            | 1, BackupCaptureFrame.Observe(nonce, cycleId, hash) ->
                return! this.Observe(nonce, cycleId, hash, cancellationToken)
            | _ ->
                return raise (InvalidOperationException("Backup capture frame is out of sequence."))
        }

    interface IDisposable with
        member _.Dispose() =
            state <- 2
            (barrier :> IDisposable).Dispose()

    static member Begin
        (
            owner: NpgsqlConnection,
            dataSource: NpgsqlDataSource,
            witness: WitnessProtocol,
            commitments: ISuppressionCommitments,
            evidence: IBackupCaptureEvidence,
            leaseId: Guid,
            firstFrame: byte array,
            now: DateTimeOffset,
            cancellationToken: CancellationToken
        ) =
        task {
            match DatabaseBackupCaptureFrames.parse firstFrame now with
            | BackupCaptureFrame.Begin(nonce, expiresAt) when leaseId <> Guid.Empty ->
                let! barrier =
                    DatabaseBackupCaptureBarrier.Acquire(
                        owner,
                        dataSource,
                        witness,
                        commitments,
                        cancellationToken
                    )

                try
                    let held =
                        BackupCaptureSessionStart.describe
                            evidence
                            barrier
                            nonce
                            leaseId
                            now
                            expiresAt

                    return
                        new DatabaseBackupCaptureSession(held, barrier, evidence),
                        DatabaseBackupCaptureResponses.held held
                with error ->
                    (barrier :> IDisposable).Dispose()
                    return raise error
            | _ ->
                return
                    raise (
                        InvalidOperationException(
                            "Backup capture must begin with one exact lease request."
                        )
                    )
        }
