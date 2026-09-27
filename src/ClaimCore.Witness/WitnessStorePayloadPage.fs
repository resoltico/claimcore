namespace ClaimCore.Witness

open System
open Npgsql
open NpgsqlTypes

module internal WitnessStorePayloadPage =
    let private bounds afterSequence (previousHash: byte array) cutoff limit =
        if
            afterSequence < 0L
            || cutoff < afterSequence
            || limit < 1
            || limit > 32
            || previousHash.Length <> 32
        then
            invalidArg (nameof limit) "Witness page bounds are invalid."

    let read
        writerConnection
        (identity: Identity)
        afterSequence
        (expectedPreviousHash: byte array)
        cutoffSequence
        limit
        : JournalPage =
        bounds afterSequence expectedPreviousHash cutoffSequence limit

        use connection = PostgresTransport.connection writerConnection
        connection.Open()
        WitnessStoreRead.checkAdmission identity connection

        use command =
            new NpgsqlCommand(
                "SELECT j.sequence,j.epoch,j.operation_id,j.phase,j.key_id,p.encrypted_payload,"
                + "j.payload_sha256,j.previous_hash,j.entry_hash,j.lineage_id,j.subject_case_id,"
                + "p.subject_case_id,j.scope_kind FROM claimcore_witness.journal j "
                + "JOIN claimcore_witness.journal_payloads p ON p.installation_id=j.installation_id "
                + "AND p.sequence=j.sequence WHERE j.installation_id=@installation "
                + "AND j.sequence>@after AND j.sequence<=@cutoff ORDER BY j.sequence LIMIT @limit",
                connection
            )

        command.Parameters.AddWithValue("installation", NpgsqlDbType.Uuid, identity.InstallationId)
        |> ignore

        command.Parameters.AddWithValue("after", NpgsqlDbType.Bigint, afterSequence)
        |> ignore

        command.Parameters.AddWithValue("cutoff", NpgsqlDbType.Bigint, cutoffSequence)
        |> ignore

        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit) |> ignore
        use reader = command.ExecuteReader()
        let items = ResizeArray<JournalRecord>()
        let mutable nextSequence = afterSequence + 1L
        let mutable previousHash = expectedPreviousHash

        while reader.Read() do
            let item = WitnessJournal.readRow identity nextSequence previousHash reader
            items.Add(item)
            nextSequence <- item.Evidence.Ticket.Sequence + 1L
            previousHash <- item.Evidence.Ticket.EntryHash

        if items.Count = 0 && afterSequence < cutoffSequence then
            invalidOp "Witness journal has a missing sequence."

        {
            Items = items |> Seq.toList
            NextAfter =
                if nextSequence - 1L < cutoffSequence then
                    Some(nextSequence - 1L)
                else
                    None
        }
