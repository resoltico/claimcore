namespace ClaimCore.Postgres

open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Witness

module internal WriterHandoffOwnerSettlementCompletion =
    let private auditedOutcome
        dataSource
        witness
        commitments
        (value: WriterHandoffSettlement)
        (ticket: Ticket)
        =
        task {
            let! audited =
                WriterHandoffOwnerSettlementChecks.verifyAfterCommit dataSource witness commitments

            return
                if audited then
                    WriterHandoffOwnerOutcome.Settled(
                        value.HandoffId,
                        ticket.Sequence,
                        ticket.EntryHash
                    )
                else
                    WriterHandoffOwnerOutcome.Unconfirmed value.HandoffId
        }

    let existing
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        dataSource
        (witness: WitnessProtocol)
        commitments
        (prepared: PrimaryWriterPreparation)
        (value: WriterHandoffSettlement)
        canonical
        signature
        (ticket: Ticket)
        =
        task {
            let alreadyPrimary =
                WriterHandoffOwnerReconcile.verifyPrimary
                    connection
                    transaction
                    value
                    canonical
                    signature
                    ticket

            if not alreadyPrimary && witness.Snapshot().TipSequence <> ticket.Sequence then
                return WriterHandoffOwnerOutcome.Unconfirmed value.HandoffId
            else
                if not alreadyPrimary then
                    WriterHandoffOwnerWrite.settlement
                        connection
                        transaction
                        prepared.Value
                        prepared.Canonical
                        prepared.Signature
                        prepared.Intent
                        canonical
                        signature
                        ticket

                    do! transaction.CommitAsync()
                else
                    transaction.Rollback()

                return! auditedOutcome dataSource witness commitments value ticket
        }

    let private persistFresh
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        ownerWitnessConnection
        (witness: WitnessProtocol)
        (prepared: PrimaryWriterPreparation)
        (value: WriterHandoffSettlement)
        canonical
        signature
        oldCapability
        newCapability
        =
        task {
            let ticket =
                WriterHandoffWitnessCommands.commit
                    ownerWitnessConnection
                    witness
                    value
                    prepared.Canonical
                    canonical
                    signature
                    oldCapability
                    newCapability

            WriterHandoffOwnerWrite.settlement
                connection
                transaction
                prepared.Value
                prepared.Canonical
                prepared.Signature
                prepared.Intent
                canonical
                signature
                ticket

            do! transaction.CommitAsync()
            return ticket
        }

    let fresh
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        dataSource
        ownerWitnessConnection
        (witness: WitnessProtocol)
        (verifier: IWriterHandoffEvidenceVerifier)
        commitments
        (prepared: PrimaryWriterPreparation)
        (value: WriterHandoffSettlement)
        canonical
        signature
        oldCapability
        newCapability
        =
        task {
            let! allowed =
                WriterHandoffOwnerSettlementChecks.preflight
                    connection
                    transaction
                    dataSource
                    witness
                    verifier
                    commitments
                    prepared
                    value
                    canonical
                    signature
                    newCapability

            if not allowed then
                return WriterHandoffOwnerOutcome.Refused
            else
                let! ticket =
                    persistFresh
                        connection
                        transaction
                        ownerWitnessConnection
                        witness
                        prepared
                        value
                        canonical
                        signature
                        oldCapability
                        newCapability

                return! auditedOutcome dataSource witness commitments value ticket
        }
