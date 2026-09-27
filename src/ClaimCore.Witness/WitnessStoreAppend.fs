namespace ClaimCore.Witness

open System
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
        =
        use command =
            new NpgsqlCommand(
                "SELECT sequence,entry_hash,payload_sha256 FROM claimcore_witness.append("
                + "@installation,@lineage,@epoch,@operation,@scope,@case,@phase,@keyId,@payload,@writerCapability)",
                writer
            )

        command.Parameters.AddWithValue("installation", NpgsqlDbType.Uuid, identity.InstallationId)
        |> ignore

        command.Parameters.AddWithValue("lineage", NpgsqlDbType.Uuid, identity.LineageId)
        |> ignore

        command.Parameters.AddWithValue("epoch", NpgsqlDbType.Bigint, identity.Epoch)
        |> ignore

        command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operation)
        |> ignore

        bindScope command phase subjectCaseId

        command.Parameters.AddWithValue("phase", NpgsqlDbType.Text, Encoding.phase phase)
        |> ignore

        command.Parameters.AddWithValue("keyId", NpgsqlDbType.Uuid, keyId) |> ignore

        command.Parameters.AddWithValue("payload", NpgsqlDbType.Bytea, encryptedPayload)
        |> ignore

        command.Parameters.AddWithValue("writerCapability", NpgsqlDbType.Bytea, writerCapability)
        |> ignore

        use result = command.ExecuteReader()

        if not (result.Read()) then
            invalidOp "Witness append returned no row."

        let committed =
            result.GetInt64(0),
            result.GetFieldValue<byte array>(1),
            result.GetFieldValue<byte array>(2)

        if result.Read() then
            invalidOp "Witness append returned duplicate rows."

        committed

    let private readback
        (writerConnection: string)
        (identity: Identity)
        (operation: Guid)
        (subjectCaseId: Guid option)
        (phase: Phase)
        (keyId: Guid)
        (encryptedPayload: byte array)
        (committed: int64 * byte array * byte array)
        =
        // A different backend must observe the committed row before admission proceeds.
        use verifier = PostgresTransport.connection writerConnection
        verifier.Open()

        let evidence =
            WitnessStoreRead.readEvidence identity verifier operation phase
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

        observed

    let append
        (writerConnection: string)
        (identity: Identity)
        (writerCapability: byte array)
        (operation: Guid)
        (subjectCaseId: Guid option)
        (phase: Phase)
        (keyId: Guid)
        (encryptedPayload: byte array)
        =
        if encryptedPayload.Length = 0 || encryptedPayload.Length > 1048576 then
            invalidArg (nameof encryptedPayload) "Witness encrypted payload size is invalid."

        use writer = PostgresTransport.connection writerConnection
        writer.Open()
        WitnessStoreRead.checkAdmission identity writer

        let committed =
            write
                writer
                identity
                writerCapability
                operation
                subjectCaseId
                phase
                keyId
                encryptedPayload

        readback
            writerConnection
            identity
            operation
            subjectCaseId
            phase
            keyId
            encryptedPayload
            committed
