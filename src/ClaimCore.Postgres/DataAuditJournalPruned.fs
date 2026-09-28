namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open NpgsqlTypes
open ClaimCore.Witness
open DataAuditCommon

module internal DataAuditJournalPruned =
    let private phaseName =
        function
        | Intent -> "INTENT"
        | SettledAccepted -> "SETTLED_ACCEPTED"
        | SettledRevoked -> "SETTLED_REVOKED"
        | SettledAuthority -> "SETTLED_AUTHORITY"
        | AbortedBeforeCommit -> "ABORTED_BEFORE_COMMIT"
        | KeyRotated -> "KEY_ROTATED"

    let private sql =
        "SELECT EXISTS (SELECT 1 FROM claimcore.case_erasure_prune_targets t "
        + "JOIN claimcore.case_erasure_tombstones e ON e.case_id=t.case_id "
        + "AND e.witness_prune_event_id=t.prune_event_id "
        + "WHERE t.case_id=@case AND t.sequence=@sequence "
        + "AND t.operation_id=@operation AND t.phase=@phase "
        + "AND t.witness_epoch=@epoch AND t.entry_hash=@hash "
        + "AND t.payload_sha256=@payload "
        + "AND e.witness_prune_cutoff_sequence>=@sequence)"

    let command connection transaction =
        let value = new NpgsqlCommand(sql, connection, transaction)
        Sql.uuid value "case" Guid.Empty
        Sql.integer value "sequence" 0L
        Sql.uuid value "operation" Guid.Empty
        Sql.text value "phase" "INTENT"
        Sql.integer value "epoch" 0L
        Sql.add value "hash" NpgsqlDbType.Bytea (box (Array.zeroCreate<byte> 32))
        Sql.add value "payload" NpgsqlDbType.Bytea (box (Array.zeroCreate<byte> 32))
        value

    let require (command: NpgsqlCommand) (item: MetadataRecord) (ct: CancellationToken) =
        task {
            let ticket = item.Ticket

            if item.PayloadPresent then
                return ()
            elif ticket.ScopeKind <> Case || ticket.SubjectCaseId.IsNone then
                corrupt ()
            else
                command.Parameters["case"].Value <- ticket.SubjectCaseId.Value
                command.Parameters["sequence"].Value <- ticket.Sequence
                command.Parameters["operation"].Value <- ticket.OperationId
                command.Parameters["phase"].Value <- phaseName ticket.Phase
                command.Parameters["epoch"].Value <- ticket.Epoch
                command.Parameters["hash"].Value <- ticket.EntryHash
                command.Parameters["payload"].Value <- ticket.PayloadHash
                let! found = command.ExecuteScalarAsync(ct)

                if not (unbox<bool> found) then
                    corrupt ()
        }
