namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Witness
open DataAuditCommon

/// A witness-only A1 is explicitly pending; after A3 every abort ticket needs an exact P receipt.
module internal DataAuditJournalAbort =
    let command (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) =
        let query =
            new NpgsqlCommand(
                "SELECT abort_sequence,abort_hash FROM claimcore.writer_handoff_aborts "
                + "WHERE handoff_id=@operation",
                connection,
                transaction
            )

        Sql.uuid query "operation" Guid.Empty
        query

    let verify (tip: Snapshot) (query: NpgsqlCommand) (ticket: Ticket) (ct: CancellationToken) =
        task {
            if ticket.ScopeKind = Installation then
                query.Parameters["operation"].Value <- ticket.OperationId
                use! reader = query.ExecuteReaderAsync(ct)

                if reader.Read() then
                    if
                        reader.GetInt64(0) <> ticket.Sequence
                        || reader.GetFieldValue<byte array>(1) <> ticket.EntryHash
                        || reader.Read()
                    then
                        corrupt ()
                elif not (tip.HandoffPending && ticket.Sequence = tip.TipSequence) then
                    corrupt ()
        }
