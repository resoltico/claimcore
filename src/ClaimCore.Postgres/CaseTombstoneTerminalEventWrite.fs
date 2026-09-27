namespace ClaimCore.Postgres

open System
open System.IO
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open OwnerTerminalDecisionData

module internal CaseTombstoneTerminalEventWrite =
    let private action =
        function
        | TombstoneTerminalProposal.ConfirmManagedPayloadAbsence _ ->
            "CONFIRM_MANAGED_PAYLOAD_ABSENCE", "PAYLOAD_ERASED_SUPPRESSION_RETAINED"
        | TombstoneTerminalProposal.CompleteSuppressionHorizon _ ->
            "COMPLETE_SUPPRESSION_HORIZON", "ERASURE_FINAL"

    let private insertEventSql =
        "INSERT INTO claimcore.case_erasure_terminal_events "
        + "(terminal_event_id,case_id,action_name,resulting_phase,executor_kind,policy_id,"
        + "suppression_until,prune_event_id,copy_inventory_digest,relevant_copy_count,"
        + "writer_generation,copy_absence_seal_sha256,recovery_fence_digest,"
        + "approval_one_id,approval_two_id,actor_authority_revision,"
        + "previous_authority_revision,previous_authority_hash,"
        + "authority_revision,authority_hash,canonical_action,candidate_sha256,"
        + "witness_sequence,witness_epoch,witness_entry_hash,recorded_at) "
        + "VALUES (@event,@case,@action,@phase,'SCHEMA_OWNER_PROCESS',@policy,@until,@prune,"
        + "@copyDigest,@copyCount,@generation,@copyProof,@fence,@approvalOne,@approvalTwo,"
        + "@actorAuthorityRevision,@previousRevision,@previousHash,@revision,@hash,@canonical,@candidate,"
        + "@sequence,@epoch,@witnessHash,@instant)"

    let private bindApprovals (command: NpgsqlCommand) (approvals: ApprovalEvidence list) =
        Sql.uuid command "approvalOne" approvals[0].ApprovalId
        Sql.uuid command "approvalTwo" approvals[1].ApprovalId

    let private bindRecoveryFence
        (command: NpgsqlCommand)
        (fence: OwnerRecoveryFenceCertificate option)
        =
        match fence with
        | None -> Sql.add command "fence" NpgsqlDbType.Bytea (box DBNull.Value)
        | Some value ->
            Sql.add
                command
                "fence"
                NpgsqlDbType.Bytea
                (box (Convert.FromHexString value.Facts.FenceDigest))

    let private bindEvent
        (command: NpgsqlCommand)
        proposal
        (copy: OwnerCopyAbsenceCertificate)
        (fence: OwnerRecoveryFenceCertificate option)
        (approvals: ApprovalEvidence list)
        actorAuthorityRevision
        (stored: StoredTerminalTombstone)
        observedAt
        (canonical: byte array)
        (intent: WitnessIntent)
        =
        let value = TombstoneTerminalProposal.copy proposal
        let actionName, phase = action proposal

        let eventHash =
            CaseTombstoneTerminalEventCandidate.eventHash stored.AuthorityHash canonical

        Sql.uuid command "event" value.EventId
        Sql.uuid command "case" value.CaseId
        Sql.text command "action" actionName
        Sql.text command "phase" phase
        Sql.text command "policy" value.PolicyId
        Sql.add command "until" NpgsqlDbType.TimestampTz (box value.SuppressionUntil)
        Sql.uuid command "prune" value.PruneEventId

        Sql.add
            command
            "copyDigest"
            NpgsqlDbType.Bytea
            (box (Convert.FromHexString value.CopyInventoryDigest))

        Sql.integer command "copyCount" value.RelevantCopyCount
        Sql.integer command "generation" value.ExpectedWriterGeneration
        Sql.add command "copyProof" NpgsqlDbType.Bytea (box copy.SignedProofSha256)

        bindRecoveryFence command fence

        bindApprovals command approvals
        Sql.integer command "actorAuthorityRevision" actorAuthorityRevision
        Sql.integer command "previousRevision" stored.AuthorityRevision
        Sql.add command "previousHash" NpgsqlDbType.Bytea (box stored.AuthorityHash)
        Sql.integer command "revision" (stored.AuthorityRevision + 1L)
        Sql.add command "hash" NpgsqlDbType.Bytea (box eventHash)
        Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
        Sql.add command "candidate" NpgsqlDbType.Bytea (box intent.CandidateHash)
        Sql.integer command "sequence" intent.Ticket.Sequence
        Sql.integer command "epoch" intent.Ticket.Epoch
        Sql.add command "witnessHash" NpgsqlDbType.Bytea (box intent.Ticket.EntryHash)
        Sql.add command "instant" NpgsqlDbType.TimestampTz (box observedAt)
        eventHash

    let private persistEvent
        connection
        transaction
        proposal
        copy
        fence
        approvals
        actorAuthorityRevision
        stored
        observedAt
        canonical
        intent
        =
        task {
            use command = new NpgsqlCommand(insertEventSql, connection, transaction)

            let hash =
                bindEvent
                    command
                    proposal
                    copy
                    fence
                    approvals
                    actorAuthorityRevision
                    stored
                    observedAt
                    canonical
                    intent

            let! rows = command.ExecuteNonQueryAsync()

            if rows <> 1 then
                raise (InvalidDataException("Terminal owner event was not inserted."))

            return hash
        }

    let private useApprovals
        connection
        transaction
        (proposal: TombstoneTerminalProposal)
        (approvals: ApprovalEvidence list)
        =
        task {
            let eventId = TombstoneTerminalProposal.eventId proposal
            let caseId = TombstoneTerminalProposal.caseId proposal

            for slot, value in
                approvals |> List.indexed |> List.map (fun (index, row) -> index + 1, row) do
                use command =
                    new NpgsqlCommand(
                        "INSERT INTO claimcore.case_erasure_terminal_approval_uses "
                        + "(approval_id,terminal_event_id,case_id,slot) "
                        + "VALUES (@approval,@event,@case,@slot)",
                        connection,
                        transaction
                    )

                Sql.uuid command "approval" value.ApprovalId
                Sql.uuid command "event" eventId
                Sql.uuid command "case" caseId
                Sql.add command "slot" NpgsqlDbType.Integer (box slot)
                let! rows = command.ExecuteNonQueryAsync()

                if rows <> 1 then
                    raise (InvalidDataException("Terminal approval use was not inserted."))
        }

    let private updateTombstone connection transaction proposal =
        task {
            let value = TombstoneTerminalProposal.copy proposal
            let _, nextPhase = action proposal

            let previous, copyEvent, finalEvent =
                match proposal with
                | TombstoneTerminalProposal.ConfirmManagedPayloadAbsence _ ->
                    "ERASURE_PENDING", box value.EventId, box DBNull.Value
                | TombstoneTerminalProposal.CompleteSuppressionHorizon _ ->
                    "PAYLOAD_ERASED_SUPPRESSION_RETAINED", box DBNull.Value, box value.EventId

            use command =
                new NpgsqlCommand(
                    "UPDATE claimcore.case_erasure_tombstones SET phase=@next,"
                    + "retention_policy_id=@policy,suppression_until=@until,"
                    + "copy_absence_event_id=COALESCE(@copy,copy_absence_event_id),"
                    + "suppression_final_event_id=COALESCE(@final,suppression_final_event_id) "
                    + "WHERE case_id=@case AND phase=@previous",
                    connection,
                    transaction
                )

            Sql.uuid command "case" value.CaseId
            Sql.text command "next" nextPhase
            Sql.text command "previous" previous
            Sql.text command "policy" value.PolicyId
            Sql.add command "until" NpgsqlDbType.TimestampTz (box value.SuppressionUntil)
            Sql.add command "copy" NpgsqlDbType.Uuid copyEvent
            Sql.add command "final" NpgsqlDbType.Uuid finalEvent
            let! rows = command.ExecuteNonQueryAsync()

            if rows <> 1 then
                raise (InvalidDataException("Terminal tombstone projection did not advance."))
        }

    let private updateTip
        connection
        transaction
        (stored: StoredTerminalTombstone)
        (hash: byte array)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "UPDATE claimcore.case_erasure_authority_tip SET revision=@revision,"
                    + "event_hash=@hash WHERE case_id=@case AND revision=@previous "
                    + "AND event_hash=@previousHash",
                    connection,
                    transaction
                )

            Sql.uuid command "case" stored.CaseId
            Sql.integer command "revision" (stored.AuthorityRevision + 1L)
            Sql.integer command "previous" stored.AuthorityRevision
            Sql.add command "hash" NpgsqlDbType.Bytea (box hash)
            Sql.add command "previousHash" NpgsqlDbType.Bytea (box stored.AuthorityHash)
            let! rows = command.ExecuteNonQueryAsync()

            if rows <> 1 then
                raise (InvalidDataException("Terminal authority tip did not advance."))
        }

    let persist
        connection
        transaction
        proposal
        copy
        fence
        approvals
        actorAuthorityRevision
        stored
        observedAt
        canonical
        intent
        =
        task {
            let! hash =
                persistEvent
                    connection
                    transaction
                    proposal
                    copy
                    fence
                    approvals
                    actorAuthorityRevision
                    stored
                    observedAt
                    canonical
                    intent

            do! useApprovals connection transaction proposal approvals
            do! updateTombstone connection transaction proposal
            do! updateTip connection transaction stored hash
        }
