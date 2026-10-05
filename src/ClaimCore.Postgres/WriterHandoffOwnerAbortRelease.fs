namespace ClaimCore.Postgres

open System
open System.Data
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness

/// A3 releases only an exact A1+A2 pair; each already-open runtime remains fenced.
module internal WriterHandoffOwnerAbortRelease =
    let private audited dataSource (witness: WitnessProtocol) commitments cutoff =
        task {
            use! audit = RuntimeDatabase.openConnectionAsync dataSource

            let! summary =
                DataAudit.runWithSuppression audit witness commitments CancellationToken.None

            return summary.PendingIntents = 0L && summary.WitnessCutoff = cutoff
        }

    let private pairMatches
        owner
        transaction
        (witness: WitnessProtocol)
        (value: WriterHandoffAbort)
        canonical
        digest
        (ticket: Ticket)
        (oldCapability: byte array)
        =
        task {
            let! snapshot = witness.Snapshot(CancellationToken.None)

            return
                WriterHandoffOwnerAbortWrite.verifyPrimary
                    owner
                    transaction
                    value
                    canonical
                    digest
                    ticket
                && SHA256.HashData(oldCapability) = value.OldCapabilitySha256
                && snapshot.WriterGeneration = value.OldGeneration
                && ((snapshot.HandoffPending
                     && snapshot.TipSequence = ticket.Sequence
                     && snapshot.TipHash = ticket.EntryHash)
                    || (not snapshot.HandoffPending
                        && snapshot.LastAbortedHandoffId = Some value.HandoffId
                        && snapshot.LastAbortedHandoffSequence = Some ticket.Sequence
                        && snapshot.LastAbortedHandoffHash = Some ticket.EntryHash
                        && snapshot.TipSequence >= ticket.Sequence))
        }

    let private finish
        dataSource
        ownerWitnessConnection
        (witness: WitnessProtocol)
        commitments
        (value: WriterHandoffAbort)
        (ticket: Ticket)
        oldCapability
        =
        task {
            let! before = witness.Snapshot(CancellationToken.None)

            if before.HandoffPending then
                do!
                    WriterHandoffWitnessAbortCommands.release
                        ownerWitnessConnection
                        witness
                        value.HandoffId
                        ticket
                        oldCapability
                        CancellationToken.None

            let! after = witness.Snapshot(CancellationToken.None)

            if
                after.HandoffPending
                || after.LastAbortedHandoffId <> Some value.HandoffId
                || after.LastAbortedHandoffSequence <> Some ticket.Sequence
                || after.LastAbortedHandoffHash <> Some ticket.EntryHash
            then
                return WriterHandoffAbortOutcome.Unconfirmed value.HandoffId
            else
                let! verified = audited dataSource witness commitments after.TipSequence

                return
                    if verified then
                        WriterHandoffAbortOutcome.Released(
                            value.HandoffId,
                            ticket.Sequence,
                            ticket.EntryHash
                        )
                    else
                        WriterHandoffAbortOutcome.Unconfirmed value.HandoffId
        }

    let private releaseIfExact
        owner
        transaction
        dataSource
        ownerWitnessConnection
        (witness: WitnessProtocol)
        commitments
        (value: WriterHandoffAbort)
        canonical
        digest
        (ticket: Ticket)
        oldCapability
        (started: bool ref)
        =
        task {
            let! matched =
                pairMatches owner transaction witness value canonical digest ticket oldCapability

            if not matched then
                return WriterHandoffAbortOutcome.Unconfirmed value.HandoffId
            else
                started.Value <- true

                return!
                    finish
                        dataSource
                        ownerWitnessConnection
                        witness
                        commitments
                        value
                        ticket
                        oldCapability
        }

    let private underLock
        owner
        transaction
        dataSource
        ownerWitnessConnection
        (witness: WitnessProtocol)
        commitments
        (value: WriterHandoffAbort)
        canonical
        signatureOne
        signatureTwo
        oldCapability
        started
        =
        task {
            let! _ = ActorGrantRead.lockRevision owner transaction true CancellationToken.None

            let! ticket, digest =
                WriterHandoffOwnerAbortBackfill.readReceipt
                    owner
                    transaction
                    witness
                    value
                    canonical
                    signatureOne
                    signatureTwo

            return!
                releaseIfExact
                    owner
                    transaction
                    dataSource
                    ownerWitnessConnection
                    witness
                    commitments
                    value
                    canonical
                    digest
                    ticket
                    oldCapability
                    started
        }

    let release
        (primaryOwner: NpgsqlConnection)
        dataSource
        ownerWitnessConnection
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments option)
        canonical
        signatureOne
        signatureTwo
        (oldCapability: byte array)
        =
        task {
            match WriterHandoffAbort.parse canonical with
            | None -> return WriterHandoffAbortOutcome.Refused
            | Some value ->
                let started = ref false

                try
                    OwnerConnection.requireIdentity primaryOwner
                    SchemaBaseline.requireCurrent primaryOwner
                    do! witness.AdmitReadOnly(CancellationToken.None)

                    use! _authorityFence =
                        AuthorityOperationFence.acquireExclusive
                            None
                            primaryOwner
                            CancellationToken.None

                    use transaction = primaryOwner.BeginTransaction(IsolationLevel.ReadCommitted)

                    return!
                        underLock
                            primaryOwner
                            transaction
                            dataSource
                            ownerWitnessConnection
                            witness
                            commitments
                            value
                            canonical
                            signatureOne
                            signatureTwo
                            oldCapability
                            started
                with _ ->
                    return
                        if started.Value then
                            WriterHandoffAbortOutcome.Unconfirmed value.HandoffId
                        else
                            WriterHandoffAbortOutcome.Refused
        }
