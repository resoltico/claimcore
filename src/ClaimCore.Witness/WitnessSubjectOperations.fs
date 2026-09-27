namespace ClaimCore.Witness

open System
open System.Collections.Generic
open System.Data
open Npgsql
open NpgsqlTypes

module internal WitnessSubjectOperations =
    let private cutoffHash
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (identity: Identity)
        cutoff
        tipSequence
        tipHash
        =
        if cutoff = 0L then
            Array.zeroCreate<byte> 32
        elif cutoff = tipSequence then
            tipHash
        else
            use command =
                new NpgsqlCommand(
                    "SELECT entry_hash FROM claimcore_witness.journal "
                    + "WHERE installation_id=@installation AND sequence=@cutoff",
                    connection,
                    transaction
                )

            command.Parameters.AddWithValue(
                "installation",
                NpgsqlDbType.Uuid,
                identity.InstallationId
            )
            |> ignore

            command.Parameters.AddWithValue("cutoff", NpgsqlDbType.Bigint, cutoff) |> ignore

            match command.ExecuteScalar() with
            | :? (byte array) as hash when hash.Length = 32 -> hash
            | _ -> invalidOp "Witness metadata cutoff is missing."

    let private snapshot
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (identity: Identity)
        =
        use command =
            new NpgsqlCommand(
                "SELECT lineage_id,epoch,tip_sequence,tip_hash "
                + "FROM claimcore_witness.installation "
                + "WHERE singleton AND installation_id=@installation",
                connection,
                transaction
            )

        command.Parameters.AddWithValue("installation", NpgsqlDbType.Uuid, identity.InstallationId)
        |> ignore

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Witness metadata installation is missing."

        let lineage = reader.GetGuid(0)
        let epoch = reader.GetInt64(1)
        let tip = reader.GetInt64(2)
        let hash = reader.GetFieldValue<byte array>(3)

        if
            reader.Read()
            || lineage <> identity.LineageId
            || epoch <> identity.Epoch
            || hash.Length <> 32
        then
            invalidOp "Witness metadata installation diverged."

        tip, hash

    let private captureIntent
        subjectCaseId
        (seen: HashSet<Guid>)
        (intents: ResizeArray<SubjectOperation>)
        (ticket: Ticket)
        =
        match subjectCaseId with
        | Some target when
            ticket.ScopeKind = Case
            && ticket.SubjectCaseId = Some target
            && ticket.Phase = Intent
            ->
            if not (seen.Add(ticket.OperationId)) then
                invalidOp "Witness page has duplicate case intents."

            intents.Add(
                {
                    OperationId = ticket.OperationId
                    Intent = ticket
                }
            )
        | _ -> ()

    let private readPage
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (identity: Identity)
        subjectCaseId
        cutoff
        afterSequence
        previousHash
        (emitPage: SubjectOperation list -> unit)
        =
        use command =
            new NpgsqlCommand(
                "SELECT sequence,epoch,operation_id,phase,key_id,payload_sha256,"
                + "previous_hash,entry_hash,lineage_id,scope_kind,subject_case_id "
                + "FROM claimcore_witness.journal WHERE installation_id=@installation "
                + "AND sequence>@after AND sequence<=@cutoff ORDER BY sequence LIMIT 32",
                connection,
                transaction
            )

        command.Parameters.AddWithValue("installation", NpgsqlDbType.Uuid, identity.InstallationId)
        |> ignore

        command.Parameters.AddWithValue("after", NpgsqlDbType.Bigint, afterSequence)
        |> ignore

        command.Parameters.AddWithValue("cutoff", NpgsqlDbType.Bigint, cutoff) |> ignore
        use reader = command.ExecuteReader()
        let intents = ResizeArray<SubjectOperation>()
        let seen = HashSet<Guid>()
        let mutable sequence = afterSequence
        let mutable hash = previousHash

        while reader.Read() do
            let ticket = WitnessJournal.readMetadataRow identity (sequence + 1L) hash reader
            sequence <- ticket.Sequence
            hash <- ticket.EntryHash

            captureIntent subjectCaseId seen intents ticket

        reader.Close()

        if sequence = afterSequence then
            invalidOp "Witness metadata chain has a missing sequence."

        if intents.Count > 0 then
            // The exact admitted unique index forbids the same operation/phase on later pages.
            // The caller must treat this page as tentative until scan returns successfully.
            emitPage (intents |> Seq.toList)

        sequence, hash, int64 intents.Count

    let scan
        writerConnection
        identity
        subjectCaseId
        cutoff
        (emitPage: SubjectOperation list -> unit)
        =
        if (subjectCaseId |> Option.exists ((=) Guid.Empty)) || cutoff < 0L then
            invalidArg (nameof subjectCaseId) "Witness subject or cutoff is invalid."

        use connection = PostgresTransport.connection writerConnection
        connection.Open()
        WitnessStoreRead.checkAdmission identity connection
        use transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead)

        use readOnly =
            new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction)

        readOnly.ExecuteNonQuery() |> ignore
        let tip, tipHash = snapshot connection transaction identity

        if cutoff > tip then
            invalidOp "Witness metadata cutoff is beyond the observed tip."

        let expectedCutoffHash =
            cutoffHash connection transaction identity cutoff tip tipHash

        let mutable sequence = 0L
        let mutable hash = Array.zeroCreate<byte> 32
        let mutable intentCount = 0L

        while sequence < cutoff do
            let nextSequence, nextHash, count =
                readPage connection transaction identity subjectCaseId cutoff sequence hash emitPage

            sequence <- nextSequence
            hash <- nextHash
            intentCount <- intentCount + count

        if sequence <> cutoff || hash <> expectedCutoffHash then
            invalidOp "Witness metadata cutoff hash diverged."

        transaction.Commit()

        {
            CutoffSequence = cutoff
            CutoffHash = hash
            IntentCount = intentCount
        }
