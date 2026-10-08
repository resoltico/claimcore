namespace ClaimCore.Witness

open System
open System.Threading
open System.Data.Common
open System.Security.Cryptography
open Npgsql
open NpgsqlTypes

module internal WitnessStoreRead =
    let lockWriterAdmission
        (identity: Identity)
        (capability: byte array)
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT claimcore_witness.acquire_read_fence(ROW(@installation,@lineage,@epoch)::claimcore_witness.installation_identity,@writerCapability)",
                    connection,
                    transaction
                )

            WitnessDatabaseAdmission.bindIdentity command identity

            command.Parameters.AddWithValue("writerCapability", NpgsqlDbType.Bytea, capability)
            |> ignore

            let! result = command.ExecuteScalarAsync(ct)

            match result with
            | :? int64 as generation when generation > 0L -> return generation
            | _ -> return invalidOp "Witness writer generation is fenced."
        }

    let checkWriterAdmission
        (identity: Identity)
        (capability: byte array)
        (connection: NpgsqlConnection)
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT writer_generation,writer_capability_sha256,handoff_pending,activation_pending,"
                    + "loss_retirement_pending,loss_retired "
                    + "FROM claimcore_witness.installation WHERE singleton "
                    + "AND installation_id=@installation AND lineage_id=@lineage AND epoch=@epoch "
                    + "AND current_user='claimcore_witness_writer'",
                    connection
                )

            WitnessDatabaseAdmission.bindIdentity command identity

            use! reader = command.ExecuteReaderAsync(ct)

            let! found = reader.ReadAsync(ct)

            if not found then
                invalidOp "Witness writer identity is unavailable."

            let generation = reader.GetInt64(0)
            let expected = reader.GetFieldValue<byte array>(1)
            let pending = reader.GetBoolean(2)
            let activationPending = reader.GetBoolean(3)
            let lossPending = reader.GetBoolean(4)
            let lossRetired = reader.GetBoolean(5)
            let actual = SHA256.HashData(capability)

            try
                let! duplicated = reader.ReadAsync(ct)

                if
                    duplicated
                    || generation < 1L
                    || pending
                    || activationPending
                    || lossPending
                    || lossRetired
                    || expected.Length <> 32
                    || not (
                        CryptographicOperations.FixedTimeEquals(expected.AsSpan(), actual.AsSpan())
                    )
                then
                    invalidOp "Witness writer generation is unavailable."
            finally
                CryptographicOperations.ZeroMemory(actual)

            return generation
        }

    let tryReadEntryHash writerConnection (identity: Identity) sequence (ct: CancellationToken) =
        task {
            if sequence < 0L then
                invalidArg (nameof sequence) "Witness sequence is invalid."

            if sequence = 0L then
                return Some(Array.zeroCreate<byte> 32)
            else
                use connection = PostgresTransport.connection writerConnection
                do! connection.OpenAsync(ct)
                do! WitnessDatabaseAdmission.checkAsync identity connection ct

                use command =
                    new NpgsqlCommand(
                        "SELECT entry_hash,epoch,lineage_id FROM claimcore_witness.journal "
                        + "WHERE installation_id=@installation AND sequence=@sequence",
                        connection
                    )

                command.Parameters.AddWithValue(
                    "installation",
                    NpgsqlDbType.Uuid,
                    identity.InstallationId
                )
                |> ignore

                command.Parameters.AddWithValue("sequence", NpgsqlDbType.Bigint, sequence)
                |> ignore

                use! reader = command.ExecuteReaderAsync(ct)

                let! found = reader.ReadAsync(ct)

                if not found then
                    return None
                else
                    let hash = reader.GetFieldValue<byte array>(0)

                    let epoch = reader.GetInt64(1)
                    let lineage = reader.GetGuid(2)
                    let! duplicated = reader.ReadAsync(ct)

                    if
                        epoch <> identity.Epoch
                        || lineage <> identity.LineageId
                        || hash.Length <> 32
                        || duplicated
                    then
                        invalidOp "Witness historical entry identity diverged."

                    return Some hash
        }

    let private readCommitted
        (identity: Identity)
        (operation: Guid)
        (phase: Phase)
        (reader: DbDataReader)
        =
        if reader.GetInt64(3) <> identity.Epoch || reader.GetGuid(4) <> identity.LineageId then
            invalidOp "Witness committed row has a different epoch or lineage."

        if reader.IsDBNull(5) then
            invalidOp "Witness encrypted evidence is absent."

        let encryptedPayload = reader.GetFieldValue<byte array>(5)

        let scopeKind = Encoding.parseScope (reader.GetString(7))
        let subjectCaseId = if reader.IsDBNull(8) then None else Some(reader.GetGuid(8))

        if (scopeKind = Case) <> subjectCaseId.IsSome || subjectCaseId = Some Guid.Empty then
            invalidOp "Witness committed scope diverged."

        if reader.GetFieldValue<byte array>(2) <> SHA256.HashData(encryptedPayload) then
            invalidOp "Witness encrypted evidence digest diverged."

        let ticket =
            {
                Sequence = reader.GetInt64(0)
                Epoch = identity.Epoch
                KeyId = reader.GetGuid(6)
                EntryHash = reader.GetFieldValue<byte array>(1)
                PayloadHash = reader.GetFieldValue<byte array>(2)
                OperationId = operation
                Phase = phase
                ScopeKind = scopeKind
                SubjectCaseId = subjectCaseId
            }

        {
            Ticket = ticket
            EncryptedPayload = encryptedPayload
        }

    let readEvidence
        (identity: Identity)
        (connection: NpgsqlConnection)
        operation
        phase
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT j.sequence,j.entry_hash,j.payload_sha256,j.epoch,j.lineage_id,"
                    + "p.encrypted_payload,j.key_id,j.scope_kind,j.subject_case_id FROM claimcore_witness.journal j "
                    + "LEFT JOIN claimcore_witness.journal_payloads p ON p.installation_id=j.installation_id "
                    + "AND p.sequence=j.sequence AND p.subject_case_id IS NOT DISTINCT FROM j.subject_case_id "
                    + "WHERE j.installation_id=@installation "
                    + "AND j.operation_id=@operation AND j.phase=@phase",
                    connection
                )

            command.Parameters.AddWithValue(
                "installation",
                NpgsqlDbType.Uuid,
                identity.InstallationId
            )
            |> ignore

            command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operation)
            |> ignore

            command.Parameters.AddWithValue("phase", NpgsqlDbType.Text, Encoding.phase phase)
            |> ignore

            use! reader = command.ExecuteReaderAsync(ct)

            let! found = reader.ReadAsync(ct)

            if not found then
                return None
            else
                let evidence = readCommitted identity operation phase reader

                let! duplicated = reader.ReadAsync(ct)

                if duplicated then
                    invalidOp "Duplicate witness row."

                return Some evidence
        }

    let readTipEvidence
        (identity: Identity)
        (connection: NpgsqlConnection)
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT operation_id,phase FROM claimcore_witness.journal "
                    + "WHERE installation_id=@installation ORDER BY sequence DESC LIMIT 1",
                    connection
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
                return None
            else
                let operation = reader.GetGuid(0)
                let phase = Encoding.parsePhase (reader.GetString(1))
                reader.Close()
                return! readEvidence identity connection operation phase ct
        }
