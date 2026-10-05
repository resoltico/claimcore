namespace ClaimCore.Postgres

open System
open System.Data
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type internal WriterHandoffAbortOutcome =
    | AwaitingPrimary of handoffId: Guid * sequence: int64 * hash: byte array
    | AwaitingRelease of handoffId: Guid * sequence: int64 * hash: byte array
    | Released of handoffId: Guid * sequence: int64 * hash: byte array
    | Refused
    | Unconfirmed of handoffId: Guid

/// A1: current owners sign the exact abort, and witness retains its pending fence.
module internal WriterHandoffOwnerAbortStage =
    let private appendAbort
        ownerWitnessConnection
        witness
        value
        canonical
        signatureOne
        signatureTwo
        oldCapability
        =
        task {
            try
                let! ticket =
                    WriterHandoffWitnessAbortCommands.abort
                        ownerWitnessConnection
                        witness
                        value
                        canonical
                        signatureOne
                        signatureTwo
                        oldCapability
                        CancellationToken.None

                return
                    WriterHandoffAbortOutcome.AwaitingPrimary(
                        value.HandoffId,
                        ticket.Sequence,
                        ticket.EntryHash
                    )
            with _ ->
                return WriterHandoffAbortOutcome.Unconfirmed value.HandoffId
        }

    let private readyForAbort
        primaryOwner
        transaction
        (witness: WitnessProtocol)
        prepared
        (value: WriterHandoffAbort)
        revision
        oldCapability
        now
        dataSource
        commitments
        canonical
        signatureOne
        signatureTwo
        =
        task {
            let! snapshot = witness.Snapshot(CancellationToken.None)

            let! primaryGeneration, _, _, _ =
                WriterHandoffOwnerReconcile.primaryState
                    primaryOwner
                    transaction
                    CancellationToken.None

            let matching =
                WriterHandoffAbortApproval.matchesPending
                    witness
                    prepared
                    value
                    snapshot
                    revision
                    oldCapability
                    now
                && primaryGeneration = value.OldGeneration

            if not matching then
                return false
            else
                do!
                    WriterHandoffAbortApproval.requireCurrentApprovals
                        primaryOwner
                        transaction
                        witness
                        value
                        canonical
                        signatureOne
                        signatureTwo

                return!
                    DataAudit.pendingHandoff dataSource witness commitments value.PrepareSequence
        }

    let private fresh
        (primaryOwner: NpgsqlConnection)
        transaction
        dataSource
        ownerWitnessConnection
        (witness: WitnessProtocol)
        commitments
        (prepared: PrimaryWriterPreparation)
        (value: WriterHandoffAbort)
        canonical
        signatureOne
        signatureTwo
        oldCapability
        revision
        now
        =
        task {
            let! ready =
                readyForAbort
                    primaryOwner
                    transaction
                    witness
                    prepared
                    value
                    revision
                    oldCapability
                    now
                    dataSource
                    commitments
                    canonical
                    signatureOne
                    signatureTwo

            if not ready then
                return WriterHandoffAbortOutcome.Refused
            else
                return!
                    appendAbort
                        ownerWitnessConnection
                        witness
                        value
                        canonical
                        signatureOne
                        signatureTwo
                        oldCapability
        }

    let private existingAbort witness prepared value canonical signatureOne signatureTwo =
        WriterHandoffOwnerAbortEvidence.read
            witness
            prepared
            value
            canonical
            signatureOne
            signatureTwo
            CancellationToken.None

    let private reconcileOrAppend
        primaryOwner
        transaction
        dataSource
        ownerWitnessConnection
        witness
        commitments
        prepared
        value
        canonical
        signatureOne
        signatureTwo
        oldCapability
        revision
        now
        =
        task {
            let! existing =
                existingAbort witness prepared value canonical signatureOne signatureTwo

            match existing with
            | Some ticket ->
                return
                    WriterHandoffAbortOutcome.AwaitingPrimary(
                        value.HandoffId,
                        ticket.Sequence,
                        ticket.EntryHash
                    )
            | None ->
                return!
                    fresh
                        primaryOwner
                        transaction
                        dataSource
                        ownerWitnessConnection
                        witness
                        commitments
                        prepared
                        value
                        canonical
                        signatureOne
                        signatureTwo
                        oldCapability
                        revision
                        now
        }

    let private startParsed
        (primaryOwner: NpgsqlConnection)
        dataSource
        ownerWitnessConnection
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments option)
        canonical
        (signatureOne: byte array)
        (signatureTwo: byte array)
        (oldCapability: byte array)
        (value: WriterHandoffAbort)
        =
        task {
            OwnerConnection.requireIdentity primaryOwner
            SchemaBaseline.requireCurrent primaryOwner
            do! witness.AdmitReadOnly(CancellationToken.None)

            use! _authorityFence =
                AuthorityOperationFence.acquireExclusive None primaryOwner CancellationToken.None

            use transaction = primaryOwner.BeginTransaction(IsolationLevel.ReadCommitted)

            let! revision =
                ActorGrantRead.lockRevision primaryOwner transaction true CancellationToken.None

            let! now = Sql.databaseNow primaryOwner transaction CancellationToken.None

            let! retained =
                WriterHandoffOwnerRead.preparation
                    primaryOwner
                    transaction
                    witness
                    value.HandoffId
                    CancellationToken.None

            let prepared =
                retained
                |> Option.defaultWith (fun () -> invalidOp "Primary handoff is absent.")

            return!
                reconcileOrAppend
                    primaryOwner
                    transaction
                    dataSource
                    ownerWitnessConnection
                    witness
                    commitments
                    prepared
                    value
                    canonical
                    signatureOne
                    signatureTwo
                    oldCapability
                    revision
                    now
        }

    let start
        primaryOwner
        dataSource
        ownerWitnessConnection
        witness
        commitments
        canonical
        signatureOne
        signatureTwo
        oldCapability
        =
        task {
            match WriterHandoffAbort.parse canonical with
            | None -> return WriterHandoffAbortOutcome.Refused
            | Some value ->
                try
                    return!
                        startParsed
                            primaryOwner
                            dataSource
                            ownerWitnessConnection
                            witness
                            commitments
                            canonical
                            signatureOne
                            signatureTwo
                            oldCapability
                            value
                with _ ->
                    return WriterHandoffAbortOutcome.Refused
        }
