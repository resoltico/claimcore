namespace ClaimCore.Witness

open System
open System.Threading
open System.Security.Cryptography
open Npgsql
open NpgsqlTypes

module internal WitnessStoreAppend =
    let private bindScope (command: NpgsqlCommand) phase subjectCaseId =
        let scopeParameter = command.Parameters.Add("scope", NpgsqlDbType.Text)

        match phase, subjectCaseId with
        | (Intent | KeyRotated), Some _ -> scopeParameter.Value <- Encoding.scope Case
        | (Intent | KeyRotated), None -> scopeParameter.Value <- Encoding.scope Installation
        | _, None -> scopeParameter.Value <- DBNull.Value
        | _, Some _ -> invalidArg (nameof subjectCaseId) "Settlement scope inherits intent."

        let caseParameter = command.Parameters.Add("case", NpgsqlDbType.Uuid)

        match subjectCaseId with
        | Some value -> caseParameter.Value <- value
        | None -> caseParameter.Value <- DBNull.Value

    let private write
        (writer: NpgsqlConnection)
        (identity: Identity)
        (writerCapability: byte array)
        (operation: Guid)
        (subjectCaseId: Guid option)
        (phase: Phase)
        (keyId: Guid)
        (encryptedPayload: byte array)
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT sequence,entry_hash,payload_sha256 FROM claimcore_witness.append(ROW(@installation,@lineage,@epoch)::claimcore_witness.installation_identity,ROW(@scope,@case)::claimcore_witness.journal_subject,ROW(@operation,@phase,@keyId,@payload)::claimcore_witness.journal_request,@writerCapability)",
                    writer
                )

            WitnessDatabaseAdmission.bindIdentity command identity

            command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operation)
            |> ignore

            bindScope command phase subjectCaseId

            command.Parameters.AddWithValue("phase", NpgsqlDbType.Text, Encoding.phase phase)
            |> ignore

            command.Parameters.AddWithValue("keyId", NpgsqlDbType.Uuid, keyId) |> ignore

            command.Parameters.AddWithValue("payload", NpgsqlDbType.Bytea, encryptedPayload)
            |> ignore

            command.Parameters.AddWithValue(
                "writerCapability",
                NpgsqlDbType.Bytea,
                writerCapability
            )
            |> ignore

            use! result = command.ExecuteReaderAsync(ct)

            let! found = result.ReadAsync(ct)

            if not found then
                invalidOp "Witness append returned no row."

            let committed =
                result.GetInt64(0),
                result.GetFieldValue<byte array>(1),
                result.GetFieldValue<byte array>(2)

            let! duplicated = result.ReadAsync(ct)

            if duplicated then
                invalidOp "Witness append returned duplicate rows."

            return committed
        }

    let private readback
        (writerConnection: string)
        (identity: Identity)
        (operation: Guid)
        (subjectCaseId: Guid option)
        (phase: Phase)
        (keyId: Guid)
        (encryptedPayload: byte array)
        (committed: int64 * byte array * byte array)
        (ct: CancellationToken)
        =
        task {
            // A different backend must observe the committed row before admission proceeds.
            use verifier = PostgresTransport.connection writerConnection
            do! verifier.OpenAsync(ct)

            let! read = WitnessStoreRead.readEvidence identity verifier operation phase ct

            let evidence =
                read
                |> Option.defaultWith (fun () -> invalidOp "Witness committed row is missing.")

            let observed = evidence.Ticket
            let sequence, hash, digest = committed

            if
                observed.Sequence <> sequence
                || observed.EntryHash <> hash
                || observed.PayloadHash <> digest
                || digest <> SHA256.HashData(encryptedPayload)
                || evidence.EncryptedPayload <> encryptedPayload
                || observed.KeyId <> keyId
                || (phase = Intent && observed.SubjectCaseId <> subjectCaseId)
            then
                invalidOp "Witness readback diverged."

            return observed
        }

    let append
        (writerConnection: string)
        (identity: Identity)
        (writerCapability: byte array)
        (operation: Guid)
        (subjectCaseId: Guid option)
        (phase: Phase)
        (keyId: Guid)
        (encryptedPayload: byte array)
        (ct: CancellationToken)
        =
        task {
            if encryptedPayload.Length = 0 || encryptedPayload.Length > 1048576 then
                invalidArg (nameof encryptedPayload) "Witness encrypted payload size is invalid."

            use writer = PostgresTransport.connection writerConnection
            do! writer.OpenAsync(ct)
            do! WitnessDatabaseAdmission.checkAsync identity writer ct

            ct.ThrowIfCancellationRequested()

            try
                // Dispatch can append durably. Request cancellation cannot relabel its exact readback.
                let! committed =
                    write
                        writer
                        identity
                        writerCapability
                        operation
                        subjectCaseId
                        phase
                        keyId
                        encryptedPayload
                        CancellationToken.None

                return!
                    readback
                        writerConnection
                        identity
                        operation
                        subjectCaseId
                        phase
                        keyId
                        encryptedPayload
                        committed
                        CancellationToken.None
            with :? OperationCanceledException as error ->
                return
                    raise (
                        InvalidOperationException(
                            "Witness append completion is unconfirmed.",
                            error
                        )
                    )
        }
