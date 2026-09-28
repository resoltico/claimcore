namespace ClaimCore.Postgres

open System
open System.IO
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open ClaimCore.Domain
open CaseTombstonePruneApprovalPolicy

/// Stores the exact already-witnessed owner approval projection in the primary transaction.
module internal CaseTombstonePruneApprovalPersistence =
    let private insertSql =
        "INSERT INTO claimcore.case_erasure_prune_approvals "
        + "(approval_id,case_id,prune_event_id,purge_event_id,cutoff_sequence,cutoff_hash,"
        + "target_count,target_digest,expected_authority_revision,expected_authority_hash,"
        + "valid_until,approver_actor_id,approver_grant_revision,approved_at,expires_at,"
        + "canonical_action,candidate_sha256,witness_sequence,witness_epoch,witness_entry_hash) "
        + "VALUES (@approval,@case,@event,@purge,@cutoff,@cutoffHash,@targetCount,"
        + "@targetDigest,@revision,@authorityHash,@validUntil,@actor,@grant,@instant,"
        + "@expires,@canonical,@candidate,@sequence,@epoch,@witnessHash)"

    let persist
        connection
        transaction
        (context: ActorCallContext)
        (value: TombstonePruneProposal)
        (approvalId: Guid)
        (expiresAt: DateTimeOffset)
        (instant: DateTimeOffset)
        (canonical: byte array)
        (intent: WitnessIntent)
        =
        task {
            use command = new NpgsqlCommand(insertSql, connection, transaction)
            Sql.uuid command "approval" approvalId
            Sql.uuid command "case" value.CaseId
            Sql.uuid command "event" value.EventId
            Sql.uuid command "purge" value.PurgeEventId
            Sql.integer command "cutoff" value.CutoffSequence
            Sql.add command "cutoffHash" NpgsqlDbType.Bytea (box (digest value.CutoffHash).Value)
            Sql.integer command "targetCount" value.TargetCount

            Sql.add
                command
                "targetDigest"
                NpgsqlDbType.Bytea
                (box (digest value.TargetDigest).Value)

            Sql.integer command "revision" value.ExpectedAuthorityRevision

            Sql.add
                command
                "authorityHash"
                NpgsqlDbType.Bytea
                (box (digest value.ExpectedAuthorityHash).Value)

            Sql.add command "validUntil" NpgsqlDbType.TimestampTz (box value.ValidUntil)
            Sql.uuid command "actor" context.Binding.ActorId
            Sql.integer command "grant" context.Binding.GrantRevision
            Sql.add command "instant" NpgsqlDbType.TimestampTz (box instant)
            Sql.add command "expires" NpgsqlDbType.TimestampTz (box expiresAt)
            Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
            Sql.add command "candidate" NpgsqlDbType.Bytea (box intent.CandidateHash)
            Sql.integer command "sequence" intent.Ticket.Sequence
            Sql.integer command "epoch" intent.Ticket.Epoch
            Sql.add command "witnessHash" NpgsqlDbType.Bytea (box intent.Ticket.EntryHash)
            let! inserted = command.ExecuteNonQueryAsync()

            if inserted <> 1 then
                raise (InvalidDataException("Witness prune approval did not persist."))
        }
