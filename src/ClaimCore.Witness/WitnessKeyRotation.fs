namespace ClaimCore.Witness

open System
open Npgsql
open NpgsqlTypes

module KeyRotation =
    let private rotationCommand
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (identity: Identity)
        operationId
        oldKeyId
        newKeyId
        newKeyCheck
        rotationEnvelope
        writerCapability
        =
        let command =
            new NpgsqlCommand(
                "SELECT sequence,entry_hash,payload_sha256 FROM claimcore_witness.rotate_key("
                + "@installation,@lineage,@epoch,@operation,@oldKey,@newKey,@check,@event,@writerCapability)",
                connection,
                transaction
            )

        command.Parameters.AddWithValue("installation", NpgsqlDbType.Uuid, identity.InstallationId)
        |> ignore

        command.Parameters.AddWithValue("lineage", NpgsqlDbType.Uuid, identity.LineageId)
        |> ignore

        command.Parameters.AddWithValue("epoch", NpgsqlDbType.Bigint, identity.Epoch)
        |> ignore

        command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId)
        |> ignore

        command.Parameters.AddWithValue("oldKey", NpgsqlDbType.Uuid, oldKeyId) |> ignore
        command.Parameters.AddWithValue("newKey", NpgsqlDbType.Uuid, newKeyId) |> ignore

        command.Parameters.AddWithValue("check", NpgsqlDbType.Bytea, newKeyCheck)
        |> ignore

        command.Parameters.AddWithValue("event", NpgsqlDbType.Bytea, rotationEnvelope)
        |> ignore

        command.Parameters.AddWithValue("writerCapability", NpgsqlDbType.Bytea, writerCapability)
        |> ignore

        command

    let private requireRotationReadback ownerConnection oldKeyId newKeyId (ticket: Ticket) =
        use verifier = PostgresTransport.connection ownerConnection
        verifier.Open()

        use check =
            new NpgsqlCommand(
                "SELECT initial_key_id,active_key_id,tip_sequence,tip_hash FROM claimcore_witness.installation "
                + "WHERE singleton",
                verifier
            )

        use row = check.ExecuteReader()

        if
            not (row.Read())
            || row.GetGuid(0) <> oldKeyId
            || row.GetGuid(1) <> newKeyId
            || row.GetInt64(2) <> ticket.Sequence
            || row.GetFieldValue<byte array>(3) <> ticket.EntryHash
        then
            invalidOp "Witness key rotation readback diverged."

    /// Owner-only rotation after an externally proven quiescent writer barrier. Both old and new
    /// keys must remain in private custody while older encrypted journal/backup rows survive.
    let rotateKey
        (ownerConnection: string)
        (identity: Identity)
        (operationId: Guid)
        (oldKeyId: Guid)
        (newKeyId: Guid)
        (newKeyCheck: byte array)
        (rotationEnvelope: byte array)
        (writerCapability: byte array)
        =
        use connection = PostgresTransport.connection ownerConnection
        connection.Open()
        use transaction = connection.BeginTransaction()

        use command =
            rotationCommand
                connection
                transaction
                identity
                operationId
                oldKeyId
                newKeyId
                newKeyCheck
                rotationEnvelope
                writerCapability

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Witness key rotation returned no ticket."

        let ticket =
            {
                Sequence = reader.GetInt64(0)
                Epoch = identity.Epoch
                KeyId = newKeyId
                EntryHash = reader.GetFieldValue<byte array>(1)
                PayloadHash = reader.GetFieldValue<byte array>(2)
                OperationId = operationId
                Phase = KeyRotated
                ScopeKind = Installation
                SubjectCaseId = None
            }

        if reader.Read() then
            invalidOp "Witness key rotation returned duplicate tickets."

        reader.Close()
        transaction.Commit()
        // This is an owner-only readback; ordinary runtime never receives the owner connection.
        requireRotationReadback ownerConnection oldKeyId newKeyId ticket
        ticket
