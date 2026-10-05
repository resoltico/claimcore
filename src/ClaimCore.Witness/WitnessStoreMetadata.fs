namespace ClaimCore.Witness

open System
open System.Threading
open System.Threading.Tasks
open System.Security.Cryptography
open Npgsql
open NpgsqlTypes

/// Payload-independent global journal pages used after an authorized, witnessed CASE prune.
/// Missing ciphertext is surfaced, never silently reclassified as deletion.
module internal WitnessStoreMetadata =
    let private metadataSelect =
        "SELECT j.sequence,j.epoch,j.operation_id,j.phase,j.key_id,"
        + "j.payload_sha256,j.previous_hash,j.entry_hash,j.lineage_id,j.scope_kind,"
        + "j.subject_case_id,p.encrypted_payload,p.subject_case_id "
        + "FROM claimcore_witness.journal j LEFT JOIN claimcore_witness.journal_payloads p "
        + "ON p.installation_id=j.installation_id AND p.sequence=j.sequence "

    let private pageCommand
        (connection: NpgsqlConnection)
        (identity: Identity)
        afterSequence
        cutoffSequence
        limit
        =
        let command =
            new NpgsqlCommand(
                metadataSelect
                + "WHERE j.installation_id=@installation AND j.sequence>@after "
                + "AND j.sequence<=@cutoff ORDER BY j.sequence LIMIT @limit",
                connection
            )

        command.Parameters.AddWithValue("installation", NpgsqlDbType.Uuid, identity.InstallationId)
        |> ignore

        command.Parameters.AddWithValue("after", NpgsqlDbType.Bigint, afterSequence)
        |> ignore

        command.Parameters.AddWithValue("cutoff", NpgsqlDbType.Bigint, cutoffSequence)
        |> ignore

        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit) |> ignore
        command

    let private record
        (identity: Identity)
        sequence
        previousHash
        (reader: Data.Common.DbDataReader)
        =
        let ticket = WitnessJournal.readMetadataRow identity sequence previousHash reader
        let payloadPresent = not (reader.IsDBNull(11))

        if payloadPresent then
            let ciphertext = reader.GetFieldValue<byte array>(11)

            let payloadCaseId =
                if reader.IsDBNull(12) then
                    None
                else
                    Some(reader.GetGuid(12))

            if
                payloadCaseId <> ticket.SubjectCaseId
                || SHA256.HashData(ciphertext) <> ticket.PayloadHash
            then
                invalidOp "Witness metadata ciphertext diverged."

        {
            Ticket = ticket
            PreviousHash = previousHash
            PayloadPresent = payloadPresent
        }

    let readPage
        writerConnection
        identity
        afterSequence
        (expectedPreviousHash: byte array)
        cutoffSequence
        limit
        (ct: CancellationToken)
        : Task<MetadataPage> =
        task {
            if
                afterSequence < 0L
                || cutoffSequence < afterSequence
                || limit < 1
                || limit > 32
                || expectedPreviousHash.Length <> 32
            then
                invalidArg (nameof limit) "Witness metadata page bounds are invalid."

            use connection = PostgresTransport.connection writerConnection
            do! connection.OpenAsync(ct)
            do! WitnessDatabaseAdmission.checkAsync identity connection ct
            use command = pageCommand connection identity afterSequence cutoffSequence limit
            use! reader = command.ExecuteReaderAsync(ct)
            let items = ResizeArray<MetadataRecord>()
            let mutable nextSequence = afterSequence + 1L
            let mutable previousHash = expectedPreviousHash

            while! reader.ReadAsync(ct) do
                let item = record identity nextSequence previousHash reader
                items.Add(item)
                nextSequence <- item.Ticket.Sequence + 1L
                previousHash <- item.Ticket.EntryHash

            if items.Count = 0 && afterSequence < cutoffSequence then
                invalidOp "Witness metadata page has a missing sequence."

            return
                {
                    Items = items |> Seq.toList
                    NextAfter =
                        if nextSequence - 1L < cutoffSequence then
                            Some(nextSequence - 1L)
                        else
                            None
                }
        }

    let private readOne writerConnection identity sql bind (ct: CancellationToken) =
        task {
            use connection = PostgresTransport.connection writerConnection
            do! connection.OpenAsync(ct)
            do! WitnessDatabaseAdmission.checkAsync identity connection ct
            use command = new NpgsqlCommand(metadataSelect + sql, connection)

            command.Parameters.AddWithValue(
                "installation",
                NpgsqlDbType.Uuid,
                identity.InstallationId
            )
            |> ignore

            bind command
            use! reader = command.ExecuteReaderAsync(ct)

            let! found = reader.ReadAsync(ct)

            if not found then
                return None
            else
                let sequence = reader.GetInt64(0)
                let previous = reader.GetFieldValue<byte array>(6)
                let value = record identity sequence previous reader

                let! duplicated = reader.ReadAsync(ct)

                if duplicated then
                    invalidOp "Witness metadata identity is duplicated."

                return Some value
        }

    /// Local row/hash proof only. A caller must also complete the global chain/cutoff audit.
    let readAt writerConnection identity sequence ct =
        if sequence < 1L then
            invalidArg (nameof sequence) "Witness metadata sequence is invalid."

        readOne
            writerConnection
            identity
            "WHERE j.installation_id=@installation AND j.sequence=@sequence"
            (fun command ->
                command.Parameters.AddWithValue("sequence", NpgsqlDbType.Bigint, sequence)
                |> ignore)
            ct

    let readOperation writerConnection identity operation phase ct =
        let sql =
            "WHERE j.installation_id=@installation AND j.operation_id=@operation "
            + "AND j.phase=@phase"

        readOne
            writerConnection
            identity
            sql
            (fun command ->
                command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operation)
                |> ignore

                command.Parameters.AddWithValue("phase", NpgsqlDbType.Text, Encoding.phase phase)
                |> ignore)
            ct
