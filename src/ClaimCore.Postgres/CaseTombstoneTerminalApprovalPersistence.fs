namespace ClaimCore.Postgres

open System
open System.IO
open Npgsql
open NpgsqlTypes
open ClaimCore.Application

module internal CaseTombstoneTerminalApprovalPersistence =
    let private sql =
        "INSERT INTO claimcore.case_erasure_terminal_approvals "
        + "(approval_id,terminal_event_id,case_id,action_name,prune_event_id,"
        + "expected_authority_revision,expected_authority_hash,installation_id,lineage_id,"
        + "witness_epoch,witness_cutoff_sequence,witness_cutoff_hash,copy_inventory_digest,"
        + "relevant_copy_count,expected_writer_generation,recovery_fence_digest,"
        + "old_writer_generation,new_writer_generation,policy_id,suppression_until,valid_until,"
        + "approver_actor_id,approver_grant_revision,approved_at,expires_at,canonical_action,"
        + "candidate_sha256,witness_sequence,approval_witness_epoch,witness_entry_hash) "
        + "VALUES (@approval,@event,@case,@action,@prune,@revision,@authorityHash,@installation,"
        + "@lineage,@epoch,@cutoff,@cutoffHash,@copyDigest,@copyCount,@writerGeneration,"
        + "@fenceDigest,@oldGeneration,@newGeneration,@policy,@suppressionUntil,@validUntil,"
        + "@actor,@grant,@instant,@expires,@canonical,@candidate,@sequence,@witnessEpoch,@witnessHash)"

    let private bindCopy (command: NpgsqlCommand) (copy: TerminalCopyProposal) =
        Sql.uuid command "event" copy.EventId
        Sql.uuid command "case" copy.CaseId
        Sql.uuid command "prune" copy.PruneEventId
        Sql.integer command "revision" copy.ExpectedAuthorityRevision

        Sql.add
            command
            "authorityHash"
            NpgsqlDbType.Bytea
            (box (Convert.FromHexString copy.ExpectedAuthorityHash))

        Sql.uuid command "installation" copy.InstallationId
        Sql.uuid command "lineage" copy.LineageId
        Sql.integer command "epoch" copy.WitnessEpoch
        Sql.integer command "cutoff" copy.WitnessCutoffSequence

        Sql.add
            command
            "cutoffHash"
            NpgsqlDbType.Bytea
            (box (Convert.FromHexString copy.WitnessCutoffHash))

        Sql.add
            command
            "copyDigest"
            NpgsqlDbType.Bytea
            (box (Convert.FromHexString copy.CopyInventoryDigest))

        Sql.integer command "copyCount" copy.RelevantCopyCount
        Sql.integer command "writerGeneration" copy.ExpectedWriterGeneration
        Sql.text command "policy" copy.PolicyId
        Sql.add command "suppressionUntil" NpgsqlDbType.TimestampTz (box copy.SuppressionUntil)
        Sql.add command "validUntil" NpgsqlDbType.TimestampTz (box copy.ValidUntil)

    let private bindAction (command: NpgsqlCommand) proposal =
        match proposal with
        | TombstoneTerminalProposal.ConfirmManagedPayloadAbsence _ ->
            Sql.text command "action" "CONFIRM_MANAGED_PAYLOAD_ABSENCE"
            Sql.add command "fenceDigest" NpgsqlDbType.Bytea (box DBNull.Value)
            Sql.add command "oldGeneration" NpgsqlDbType.Bigint (box DBNull.Value)
            Sql.add command "newGeneration" NpgsqlDbType.Bigint (box DBNull.Value)
        | TombstoneTerminalProposal.CompleteSuppressionHorizon final ->
            Sql.text command "action" "COMPLETE_SUPPRESSION_HORIZON"

            Sql.add
                command
                "fenceDigest"
                NpgsqlDbType.Bytea
                (box (Convert.FromHexString final.RecoveryFenceDigest))

            Sql.integer command "oldGeneration" final.OldWriterGeneration
            Sql.integer command "newGeneration" final.NewWriterGeneration

    let persist
        connection
        transaction
        (context: ActorCallContext)
        proposal
        approvalId
        expiresAt
        instant
        (canonical: byte array)
        (intent: WitnessIntent)
        =
        task {
            use command = new NpgsqlCommand(sql, connection, transaction)
            Sql.uuid command "approval" approvalId
            bindCopy command (TombstoneTerminalProposal.copy proposal)
            bindAction command proposal
            Sql.uuid command "actor" context.Binding.ActorId
            Sql.integer command "grant" context.Binding.GrantRevision
            Sql.add command "instant" NpgsqlDbType.TimestampTz (box instant)
            Sql.add command "expires" NpgsqlDbType.TimestampTz (box expiresAt)
            Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
            Sql.add command "candidate" NpgsqlDbType.Bytea (box intent.CandidateHash)
            Sql.integer command "sequence" intent.Ticket.Sequence
            Sql.integer command "witnessEpoch" intent.Ticket.Epoch
            Sql.add command "witnessHash" NpgsqlDbType.Bytea (box intent.Ticket.EntryHash)
            let! inserted = command.ExecuteNonQueryAsync()

            if inserted <> 1 then
                raise (InvalidDataException("Terminal steward approval did not persist."))
        }
