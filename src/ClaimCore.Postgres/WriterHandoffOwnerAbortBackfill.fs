namespace ClaimCore.Postgres

open System
open System.Data
open Npgsql
open ClaimCore.Witness

/// A2 is a deterministic primary-only reconciliation of immutable witnessed A1.
module internal WriterHandoffOwnerAbortBackfill =
    let private exactPrimary
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (value: WriterHandoffAbort)
        canonical
        digest
        (ticket: Ticket)
        =
        use command =
            new NpgsqlCommand(
                "SELECT abort_canonical,abort_candidate_sha256,abort_sequence,abort_hash,"
                + "approval_one_id,approval_two_id FROM claimcore.writer_handoff_aborts "
                + "WHERE handoff_id=@handoff",
                connection,
                transaction
            )

        Sql.uuid command "handoff" value.HandoffId
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            false
        else
            let exact =
                reader.GetFieldValue<byte array>(0) = canonical
                && reader.GetFieldValue<byte array>(1) = digest
                && reader.GetInt64(2) = ticket.Sequence
                && reader.GetFieldValue<byte array>(3) = ticket.EntryHash
                && reader.GetGuid(4) = value.ApprovalOneId
                && reader.GetGuid(5) = value.ApprovalTwoId

            if reader.Read() || not exact then
                invalidOp "Primary abort receipt differs."

            true

    let private projection
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (value: WriterHandoffAbort)
        (ticket: Ticket)
        =
        use command =
            new NpgsqlCommand(
                "SELECT writer_generation,last_aborted_handoff_id,"
                + "last_aborted_handoff_sequence,last_aborted_handoff_hash "
                + "FROM claimcore.installation_lineage WHERE singleton",
                connection,
                transaction
            )

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Primary abort projection is absent."

        let generation = reader.GetInt64(0)
        let id = if reader.IsDBNull(1) then None else Some(reader.GetGuid(1))

        let sequence =
            if reader.IsDBNull(2) then
                None
            else
                Some(reader.GetInt64(2))

        let hash =
            if reader.IsDBNull(3) then
                None
            else
                Some(reader.GetFieldValue<byte array>(3))

        if reader.Read() || generation <> value.OldGeneration then
            invalidOp "Primary writer generation differs at abort."

        id = Some value.HandoffId
        && sequence = Some ticket.Sequence
        && hash = Some ticket.EntryHash

    let verifyPrimary connection transaction value canonical digest ticket =
        exactPrimary connection transaction value canonical digest ticket
        && projection connection transaction value ticket

    let private permittedWitness
        (witness: WitnessProtocol)
        (value: WriterHandoffAbort)
        (ticket: Ticket)
        =
        let snapshot = witness.Snapshot()

        snapshot.HandoffPending
        && snapshot.WriterGeneration = value.OldGeneration
        && snapshot.TipSequence = ticket.Sequence
        && snapshot.TipHash = ticket.EntryHash

    let private releasedWitness
        (witness: WitnessProtocol)
        (value: WriterHandoffAbort)
        (ticket: Ticket)
        =
        let snapshot = witness.Snapshot()

        not snapshot.HandoffPending
        && snapshot.WriterGeneration = value.OldGeneration
        && snapshot.LastAbortedHandoffId = Some value.HandoffId
        && snapshot.LastAbortedHandoffSequence = Some ticket.Sequence
        && snapshot.LastAbortedHandoffHash = Some ticket.EntryHash
        && snapshot.TipSequence >= ticket.Sequence

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
        let prepared =
            WriterHandoffOwnerRead.preparation owner transaction witness value.HandoffId
            |> Option.defaultWith (fun () -> invalidOp "Primary handoff is absent.")

        let ticket =
            WriterHandoffOwnerAbortEvidence.read
                witness
                prepared
                value
                canonical
                signatureOne
                signatureTwo
            |> Option.defaultWith (fun () -> invalidOp "Witness abort A1 is absent.")

        let digest =
            WriterHandoffWitnessAbortCommands.candidate canonical signatureOne signatureTwo

        ticket, digest

    let private generationMatches owner transaction (value: WriterHandoffAbort) =
        let generation, _, _, _ = WriterHandoffOwnerReconcile.primaryState owner transaction
        generation = value.OldGeneration

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
            if exactPrimary owner transaction value canonical digest ticket then
                return
                    if
                        projection owner transaction value ticket
                        && (permittedWitness witness value ticket
                            || releasedWitness witness value ticket)
                    then
                        awaiting value ticket
                    else
                        WriterHandoffAbortOutcome.Unconfirmed value.HandoffId
            elif not (permittedWitness witness value ticket) then
                return WriterHandoffAbortOutcome.Unconfirmed value.HandoffId
            else if not (generationMatches owner transaction value) then
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

            let ticket, digest =
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
                    witness.AdmitReadOnly()
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
