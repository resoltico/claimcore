namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open ClaimCore.Domain

module internal CaseErasureFenceWrite =
    let private insertSql =
        "INSERT INTO claimcore.case_erasure_tombstones "
        + "(case_id,suppression_key_id,reference_commitment,phase,source_revision,"
        + "source_disposition,lifecycle_sequence,lifecycle_hash,denial_count,"
        + "denial_set_sha256,request_event_id,request_candidate_sha256,"
        + "request_witness_sequence,request_witness_epoch,request_witness_entry_hash) "
        + "VALUES (@case,@key,@reference,'ERASURE_REQUESTED',@revision,@disposition,"
        + "@sequence,@hash,@count,@denials,@event,@candidate,@witnessSequence,"
        + "@witnessEpoch,@witnessHash)"

    let private bind
        (command: NpgsqlCommand)
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        (decision: LifecycleDecisionResult)
        revision
        eventHash
        candidateHash
        (intent: WitnessIntent)
        (commitments: ISuppressionCommitments)
        denialCount
        (denialDigest: byte array)
        =
        let referenceCommitment = commitments.Reference change.CaseReference

        if referenceCommitment.Length <> 32 || denialDigest.Length <> 32 then
            invalidOp "Erasure suppression evidence is invalid."

        Sql.uuid command "case" projection.CaseId
        Sql.uuid command "key" commitments.KeyId
        Sql.add command "reference" NpgsqlDbType.Bytea (box referenceCommitment)
        Sql.integer command "revision" revision

        Sql.text
            command
            "disposition"
            (CaseLifecycleCandidate.dispositionName (CaseLifecycle.disposition decision.State))

        Sql.integer command "sequence" (projection.Sequence + 1L)
        Sql.add command "hash" NpgsqlDbType.Bytea (box eventHash)
        Sql.integer command "count" denialCount
        Sql.add command "denials" NpgsqlDbType.Bytea (box denialDigest)
        Sql.uuid command "event" change.EventId
        Sql.add command "candidate" NpgsqlDbType.Bytea (box candidateHash)
        Sql.integer command "witnessSequence" intent.Ticket.Sequence
        Sql.integer command "witnessEpoch" intent.Ticket.Epoch
        Sql.add command "witnessHash" NpgsqlDbType.Bytea (box intent.Ticket.EntryHash)

    let persist
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        (decision: LifecycleDecisionResult)
        revision
        eventHash
        candidateHash
        (intent: WitnessIntent)
        (commitments: ISuppressionCommitments)
        (denialCount: int64)
        (denialDigest: byte array)
        =
        task {
            use command = new NpgsqlCommand(insertSql, connection, transaction)

            bind
                command
                projection
                change
                decision
                revision
                eventHash
                candidateHash
                intent
                commitments
                denialCount
                denialDigest

            let! inserted = command.ExecuteNonQueryAsync()

            if inserted <> 1 then
                invalidOp "Erasure fence was not retained."

            let! count, digest =
                CaseErasureDenials.scan
                    connection
                    transaction
                    projection.CaseId
                    commitments
                    true
                    CancellationToken.None

            if
                count <> denialCount
                || not (
                    CryptographicOperations.FixedTimeEquals(
                        ReadOnlySpan<byte>(digest),
                        ReadOnlySpan<byte>(denialDigest)
                    )
                )
            then
                invalidOp "Erasure operation denial set changed before commit."
        }
