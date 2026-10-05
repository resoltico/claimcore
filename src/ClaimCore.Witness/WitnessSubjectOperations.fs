namespace ClaimCore.Witness

open System
open System.Collections.Generic
open System.Data
open System.Threading
open System.Threading.Tasks
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
        (ct: CancellationToken)
        =
        task {
            if cutoff = 0L then
                return Array.zeroCreate<byte> 32
            elif cutoff = tipSequence then
                return tipHash
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

                let! result = command.ExecuteScalarAsync(ct)

                match result with
                | :? (byte array) as hash when hash.Length = 32 -> return hash
                | _ -> return invalidOp "Witness metadata cutoff is missing."
        }

    let private snapshot
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (identity: Identity)
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT lineage_id,epoch,tip_sequence,tip_hash "
                    + "FROM claimcore_witness.installation "
                    + "WHERE singleton AND installation_id=@installation",
                    connection,
                    transaction
                )

            command.Parameters.AddWithValue(
                "installation",
                NpgsqlDbType.Uuid,
                identity.InstallationId
            )
            |> ignore

            use! reader = command.ExecuteReaderAsync(ct)

            let! found = reader.ReadAsync(ct)

            if not found then
                invalidOp "Witness metadata installation is missing."

            let lineage = reader.GetGuid(0)
            let epoch = reader.GetInt64(1)
            let tip = reader.GetInt64(2)
            let hash = reader.GetFieldValue<byte array>(3)

            let! duplicated = reader.ReadAsync(ct)

            if
                duplicated
                || lineage <> identity.LineageId
                || epoch <> identity.Epoch
                || hash.Length <> 32
            then
                invalidOp "Witness metadata installation diverged."

            return tip, hash
        }

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
        (emitPage: SubjectOperation list -> Task<unit>)
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT sequence,epoch,operation_id,phase,key_id,payload_sha256,"
                    + "previous_hash,entry_hash,lineage_id,scope_kind,subject_case_id "
                    + "FROM claimcore_witness.journal WHERE installation_id=@installation "
                    + "AND sequence>@after AND sequence<=@cutoff ORDER BY sequence LIMIT 32",
                    connection,
                    transaction
                )

            command.Parameters.AddWithValue(
                "installation",
                NpgsqlDbType.Uuid,
                identity.InstallationId
            )
            |> ignore

            command.Parameters.AddWithValue("after", NpgsqlDbType.Bigint, afterSequence)
            |> ignore

            command.Parameters.AddWithValue("cutoff", NpgsqlDbType.Bigint, cutoff) |> ignore
            use! reader = command.ExecuteReaderAsync(ct)
            let intents = ResizeArray<SubjectOperation>()
            let seen = HashSet<Guid>()
            let mutable sequence = afterSequence
            let mutable hash = previousHash

            while! reader.ReadAsync(ct) do
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
                do! emitPage (intents |> Seq.toList)

            return sequence, hash, int64 intents.Count
        }

    let private scanPages connection transaction identity subjectCaseId cutoff emitPage ct =
        task {
            let mutable sequence = 0L
            let mutable hash = Array.zeroCreate<byte> 32
            let mutable intentCount = 0L

            while sequence < cutoff do
                let! nextSequence, nextHash, count =
                    readPage
                        connection
                        transaction
                        identity
                        subjectCaseId
                        cutoff
                        sequence
                        hash
                        emitPage
                        ct

                sequence <- nextSequence
                hash <- nextHash
                intentCount <- intentCount + count

            return sequence, hash, intentCount
        }

    let scan
        writerConnection
        identity
        subjectCaseId
        cutoff
        (emitPage: SubjectOperation list -> Task<unit>)
        (ct: CancellationToken)
        =
        task {
            if (subjectCaseId |> Option.exists ((=) Guid.Empty)) || cutoff < 0L then
                invalidArg (nameof subjectCaseId) "Witness subject or cutoff is invalid."

            use connection = PostgresTransport.connection writerConnection
            do! connection.OpenAsync(ct)
            do! WitnessDatabaseAdmission.checkAsync identity connection ct

            use! transaction =
                connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct)

            use readOnly =
                new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction)

            let! _ = readOnly.ExecuteNonQueryAsync(ct)
            let! tip, tipHash = snapshot connection transaction identity ct

            if cutoff > tip then
                invalidOp "Witness metadata cutoff is beyond the observed tip."

            let! expectedCutoffHash =
                cutoffHash connection transaction identity cutoff tip tipHash ct

            let! sequence, hash, intentCount =
                scanPages connection transaction identity subjectCaseId cutoff emitPage ct

            if sequence <> cutoff || hash <> expectedCutoffHash then
                invalidOp "Witness metadata cutoff hash diverged."

            do! transaction.CommitAsync(ct)

            return
                {
                    CutoffSequence = cutoff
                    CutoffHash = hash
                    IntentCount = intentCount
                }
        }
