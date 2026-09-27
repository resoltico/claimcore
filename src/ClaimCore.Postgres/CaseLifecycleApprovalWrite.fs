namespace ClaimCore.Postgres

open System
open System.IO
open Npgsql
open NpgsqlTypes
open ClaimCore.Application

module internal CaseLifecycleApprovalWrite =
    let private one connection transaction sql bind =
        task {
            use command = new NpgsqlCommand(sql, connection, transaction)
            bind command
            let! affected = command.ExecuteNonQueryAsync()

            if affected <> 1 then
                raise (InvalidDataException("Lifecycle approval persistence was incomplete."))
        }

    let persistApproval
        connection
        transaction
        (projection: LifecycleProjection)
        (change: LifecycleChange)
        approvalId
        expiresAt
        instant
        (actor: ActorCallContext)
        draftHash
        canonical
        (intent: WitnessIntent)
        =
        one
            connection
            transaction
            ("INSERT INTO claimcore.case_lifecycle_approvals "
             + "(approval_id,operation_id,case_id,action_name,expected_revision,"
             + "expected_lifecycle_sequence,expected_lifecycle_hash,event_digest,"
             + "approver_actor_id,approver_grant_revision,approved_at,expires_at,"
             + "canonical_action,candidate_sha256,witness_sequence,witness_epoch,witness_entry_hash) "
             + "VALUES (@approval,@operation,@caseId,@action,@revision,@sequence,@hash,"
             + "@digest,@approver,@grant,@approvedAt,@expiresAt,@canonical,@candidate,"
             + "@witnessSequence,@witnessEpoch,@witnessHash)")
            (fun command ->
                Sql.uuid command "approval" approvalId
                Sql.uuid command "operation" change.EventId
                Sql.uuid command "caseId" projection.CaseId
                Sql.text command "action" (CaseLifecycleCandidate.actionName change.Action)
                Sql.integer command "revision" change.ExpectedRevision
                Sql.integer command "sequence" change.ExpectedLifecycleSequence
                Sql.add command "hash" NpgsqlDbType.Bytea (box projection.EventHash)
                Sql.add command "digest" NpgsqlDbType.Bytea (box draftHash)
                Sql.uuid command "approver" actor.Binding.ActorId
                Sql.integer command "grant" actor.Binding.GrantRevision
                Sql.add command "approvedAt" NpgsqlDbType.TimestampTz (box instant)
                Sql.add command "expiresAt" NpgsqlDbType.TimestampTz (box expiresAt)
                Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
                Sql.add command "candidate" NpgsqlDbType.Bytea (box intent.CandidateHash)
                Sql.integer command "witnessSequence" intent.Ticket.Sequence
                Sql.integer command "witnessEpoch" intent.Ticket.Epoch
                Sql.add command "witnessHash" NpgsqlDbType.Bytea (box intent.Ticket.EntryHash))
