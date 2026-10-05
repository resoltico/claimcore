namespace ClaimCore.Postgres

open System
open System.Data
open System.Threading
open Npgsql
open ClaimCore.Witness

/// A2 is a deterministic primary-only reconciliation of immutable witnessed A1.
module internal WriterHandoffOwnerAbortBackfill =
    open WriterHandoffOwnerAbortWrite

    let private permittedWitness
        (witness: WitnessProtocol)
        (value: WriterHandoffAbort)
        (ticket: Ticket)
        =
        task {
            let! snapshot = witness.Snapshot(CancellationToken.None)

            return
                snapshot.HandoffPending
                && snapshot.WriterGeneration = value.OldGeneration
                && snapshot.TipSequence = ticket.Sequence
                && snapshot.TipHash = ticket.EntryHash
        }

    let private releasedWitness
        (witness: WitnessProtocol)
        (value: WriterHandoffAbort)
        (ticket: Ticket)
        =
        task {
            let! snapshot = witness.Snapshot(CancellationToken.None)

            return
                not snapshot.HandoffPending
                && snapshot.WriterGeneration = value.OldGeneration
                && snapshot.LastAbortedHandoffId = Some value.HandoffId
                && snapshot.LastAbortedHandoffSequence = Some ticket.Sequence
                && snapshot.LastAbortedHandoffHash = Some ticket.EntryHash
                && snapshot.TipSequence >= ticket.Sequence
        }

    let private awaiting (value: WriterHandoffAbort) (ticket: Ticket) =
        WriterHandoffAbortOutcome.AwaitingRelease(
            value.HandoffId,
            ticket.Sequence,
            ticket.EntryHash
        )

    let readReceipt
        (owner: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (value: WriterHandoffAbort)
        canonical
        signatureOne
        signatureTwo
        =
        task {
            let! retained =
                WriterHandoffOwnerRead.preparation
                    owner
                    transaction
                    witness
                    value.HandoffId
                    CancellationToken.None

            let prepared =
                retained
                |> Option.defaultWith (fun () -> invalidOp "Primary handoff is absent.")

            let! evidence =
                WriterHandoffOwnerAbortEvidence.read
                    witness
                    prepared
                    value
                    canonical
                    signatureOne
                    signatureTwo
                    CancellationToken.None

            let ticket =
                evidence
                |> Option.defaultWith (fun () -> invalidOp "Witness abort A1 is absent.")

            let digest =
                WriterHandoffWitnessAbortCommands.candidate canonical signatureOne signatureTwo

            return ticket, digest
        }

    let private generationMatches owner transaction (value: WriterHandoffAbort) =
        task {
            let! generation, _, _, _ =
                WriterHandoffOwnerReconcile.primaryState owner transaction CancellationToken.None

            return generation = value.OldGeneration
        }

    let private apply
        owner
        transaction
        witness
        (value: WriterHandoffAbort)
        canonical
        signatureOne
        signatureTwo
        (ticket: Ticket)
        digest
        (started: bool ref)
        =
        task {
            let! permitted = permittedWitness witness value ticket
            let! released = releasedWitness witness value ticket
            let! generationValid = generationMatches owner transaction value

            if exactPrimary owner transaction value canonical digest ticket then
                return
                    if projection owner transaction value ticket && (permitted || released) then
                        awaiting value ticket
                    else
                        WriterHandoffAbortOutcome.Unconfirmed value.HandoffId
            elif not permitted then
                return WriterHandoffAbortOutcome.Unconfirmed value.HandoffId
            else if not generationValid then
                return WriterHandoffAbortOutcome.Unconfirmed value.HandoffId
            else
                do!
                    WriterHandoffOwnerAbortEvidence.verifyHistoricalOwners
                        owner
                        transaction
                        witness
                        value
                        canonical
                        signatureOne
                        signatureTwo
                        CancellationToken.None

                started.Value <- true

                WriterHandoffOwnerAbortWrite.insert
                    owner
                    transaction
                    value
                    canonical
                    signatureOne
                    signatureTwo
                    ticket

                do! transaction.CommitAsync()
                return awaiting value ticket
        }

    let private underLock
        (owner: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (value: WriterHandoffAbort)
        canonical
        signatureOne
        signatureTwo
        started
        =
        task {
            let! _ =
                ActorGrantRead.lockRevision owner transaction true Threading.CancellationToken.None

            let! ticket, digest =
                readReceipt owner transaction witness value canonical signatureOne signatureTwo

            return!
                apply
                    owner
                    transaction
                    witness
                    value
                    canonical
                    signatureOne
                    signatureTwo
                    ticket
                    digest
                    started
        }

    let commit
        (primaryOwner: NpgsqlConnection)
        (witness: WitnessProtocol)
        canonical
        signatureOne
        signatureTwo
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
                        AuthorityOperationFence.acquireShared
                            None
                            primaryOwner
                            CancellationToken.None

                    use transaction = primaryOwner.BeginTransaction(IsolationLevel.ReadCommitted)

                    return!
                        underLock
                            primaryOwner
                            transaction
                            witness
                            value
                            canonical
                            signatureOne
                            signatureTwo
                            started
                with _ ->
                    return
                        if started.Value then
                            WriterHandoffAbortOutcome.Unconfirmed value.HandoffId
                        else
                            WriterHandoffAbortOutcome.Refused
        }
