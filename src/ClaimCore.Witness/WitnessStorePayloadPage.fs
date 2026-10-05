namespace ClaimCore.Witness

open System
open System.Threading
open System.Threading.Tasks
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

    let private bindPage
        (command: NpgsqlCommand)
        (identity: Identity)
        afterSequence
        cutoffSequence
        limit
        =
        command.Parameters.AddWithValue("installation", NpgsqlDbType.Uuid, identity.InstallationId)
        |> ignore

        command.Parameters.AddWithValue("after", NpgsqlDbType.Bigint, afterSequence)
        |> ignore

        command.Parameters.AddWithValue("cutoff", NpgsqlDbType.Bigint, cutoffSequence)
        |> ignore

        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit) |> ignore

    let read
        writerConnection
        (identity: Identity)
        afterSequence
        (expectedPreviousHash: byte array)
        cutoffSequence
        limit
        (ct: CancellationToken)
        : Task<JournalPage> =
        task {
            bounds afterSequence expectedPreviousHash cutoffSequence limit

            use connection = PostgresTransport.connection writerConnection
            do! connection.OpenAsync(ct)
            do! WitnessDatabaseAdmission.checkAsync identity connection ct

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

            bindPage command identity afterSequence cutoffSequence limit
            use! reader = command.ExecuteReaderAsync(ct)
            let items = ResizeArray<JournalRecord>()
            let mutable nextSequence = afterSequence + 1L
            let mutable previousHash = expectedPreviousHash

            while! reader.ReadAsync(ct) do
                let item = WitnessJournal.readRow identity nextSequence previousHash reader
                items.Add(item)
                nextSequence <- item.Evidence.Ticket.Sequence + 1L
                previousHash <- item.Evidence.Ticket.EntryHash

            if items.Count = 0 && afterSequence < cutoffSequence then
                invalidOp "Witness journal has a missing sequence."

            return
                ({
                    Items = items |> Seq.toList
                    NextAfter =
                        if nextSequence - 1L < cutoffSequence then
                            Some(nextSequence - 1L)
                        else
                            None
                }
                : JournalPage)
        }
