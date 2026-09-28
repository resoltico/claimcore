namespace ClaimCore.Postgres

open System
open System.IO
open System.Security.Cryptography
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open ClaimCore.Witness

module internal CaseTombstonePrunePrimaryWrite =
    let private invalid () =
        raise (InvalidDataException("Witness prune primary receipt diverged."))

    let private digest text =
        CaseTombstonePruneApprovalPolicy.digest text |> Option.defaultWith invalid

    let private updateSql =
        "UPDATE claimcore.case_erasure_tombstones SET "
        + "witness_prune_event_id=@event,witness_prune_candidate_sha256=@candidate,"
        + "witness_prune_canonical_action=@canonical,"
        + "witness_prune_intent_sequence=@intentSequence,"
        + "witness_prune_intent_epoch=@intentEpoch,witness_prune_intent_hash=@intentHash,"
        + "witness_prune_cutoff_sequence=@cutoff,witness_prune_cutoff_hash=@cutoffHash,"
        + "witness_prune_target_count=@targetCount,witness_prune_target_digest=@targetDigest,"
        + "witness_prune_copy_inventory_sha256=@copyDigest,"
        + "witness_prune_authority_revision=@revision,"
        + "witness_prune_authority_hash=@authorityHash,witness_prune_valid_until=@validUntil "
        + "WHERE case_id=@case AND purge_event_id=@purge AND phase='ERASURE_PENDING' "
        + "AND witness_prune_event_id IS NULL"

    let private insertSql =
        "INSERT INTO claimcore.case_erasure_prune_targets "
        + "(case_id,prune_event_id,sequence,operation_id,phase,witness_epoch,"
        + "entry_hash,payload_sha256,is_external_publication) VALUES "
        + "(@case,@event,@sequence,@operation,@phase,@epoch,@hash,@payload,@external)"

    let private phase =
        function
        | Intent -> "INTENT"
        | SettledAccepted -> "SETTLED_ACCEPTED"
        | SettledRevoked -> "SETTLED_REVOKED"
        | SettledAuthority -> "SETTLED_AUTHORITY"
        | AbortedBeforeCommit -> "ABORTED_BEFORE_COMMIT"
        | KeyRotated -> invalid ()

    let private update
        connection
        transaction
        (proposal: TombstonePruneProposal)
        (copyDigest: byte array)
        (canonical: byte array)
        (intent: WitnessIntent)
        =
        task {
            use command = new NpgsqlCommand(updateSql, connection, transaction)
            Sql.uuid command "event" proposal.EventId
            Sql.add command "candidate" NpgsqlDbType.Bytea (box intent.CandidateHash)
            Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
            Sql.integer command "intentSequence" intent.Ticket.Sequence
            Sql.integer command "intentEpoch" intent.Ticket.Epoch
            Sql.add command "intentHash" NpgsqlDbType.Bytea (box intent.Ticket.EntryHash)
            Sql.integer command "cutoff" proposal.CutoffSequence
            Sql.add command "cutoffHash" NpgsqlDbType.Bytea (box (digest proposal.CutoffHash))
            Sql.integer command "targetCount" proposal.TargetCount
            Sql.add command "targetDigest" NpgsqlDbType.Bytea (box (digest proposal.TargetDigest))
            Sql.add command "copyDigest" NpgsqlDbType.Bytea (box copyDigest)
            Sql.integer command "revision" proposal.ExpectedAuthorityRevision

            Sql.add
                command
                "authorityHash"
                NpgsqlDbType.Bytea
                (box (digest proposal.ExpectedAuthorityHash))

            Sql.add command "validUntil" NpgsqlDbType.TimestampTz (box proposal.ValidUntil)
            Sql.uuid command "case" proposal.CaseId
            Sql.uuid command "purge" proposal.PurgeEventId
            let! changed = command.ExecuteNonQueryAsync()

            if changed <> 1 then
                invalid ()
        }

    let private insertPage
        connection
        transaction
        (proposal: TombstonePruneProposal)
        (targets: WitnessPruneTarget list)
        =
        for target in targets do
            use command = new NpgsqlCommand(insertSql, connection, transaction)
            Sql.uuid command "case" proposal.CaseId
            Sql.uuid command "event" proposal.EventId
            Sql.integer command "sequence" target.Sequence
            Sql.uuid command "operation" target.OperationId
            Sql.text command "phase" (phase target.Phase)
            Sql.integer command "epoch" target.Epoch
            Sql.add command "hash" NpgsqlDbType.Bytea (box target.EntryHash)
            Sql.add command "payload" NpgsqlDbType.Bytea (box target.PayloadHash)
            Sql.add command "external" NpgsqlDbType.Boolean (box target.IsExternalPublication)

            if command.ExecuteNonQuery() <> 1 then
                invalid ()

    let persist
        connection
        transaction
        (witness: WitnessProtocol)
        (proposal: TombstonePruneProposal)
        (copyDigest: byte array)
        (canonical: byte array)
        (intent: WitnessIntent)
        =
        task {
            if
                copyDigest.Length <> 32
                || intent.Ticket.OperationId <> proposal.EventId
                || intent.Ticket.Phase <> Intent
                || intent.Ticket.ScopeKind <> Case
                || intent.Ticket.SubjectCaseId <> Some proposal.CaseId
                || intent.Ticket.Sequence <= proposal.CutoffSequence
                || intent.CandidateHash <> SHA256.HashData(canonical)
            then
                invalid ()

            do! update connection transaction proposal copyDigest canonical intent

            let seal =
                CaseWitnessPayloadTargets.scan
                    witness
                    proposal.CaseId
                    proposal.CutoffSequence
                    (digest proposal.CutoffHash)
                    (insertPage connection transaction proposal)

            if
                seal.TargetCount <> proposal.TargetCount
                || seal.TargetDigest <> digest proposal.TargetDigest
            then
                invalid ()
        }
