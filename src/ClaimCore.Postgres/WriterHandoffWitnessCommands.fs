namespace ClaimCore.Postgres

open System
open System.Data.Common
open System.Security.Cryptography
open Npgsql
open NpgsqlTypes
open ClaimCore.Witness

/// Low-level owner procedure binding. Caller must first verify publication, signed fence,
/// exact human approvals, primary full audit and private capability custody.
module internal WriterHandoffWitnessCommands =
    let private ticket (reader: DbDataReader) operation keyId phase =
        if not (reader.Read()) then
            invalidOp "Witness writer handoff ticket is absent."

        let result =
            {
                Sequence = reader.GetInt64(0)
                Epoch = 0L
                KeyId = keyId
                EntryHash = reader.GetFieldValue<byte array>(1)
                PayloadHash = reader.GetFieldValue<byte array>(2)
                OperationId = operation
                Phase = phase
                ScopeKind = Installation
                SubjectCaseId = None
            }

        if reader.Read() then
            invalidOp "Witness writer handoff ticket is duplicated."

        result

    let private common (command: NpgsqlCommand) identity handoffId keyId =
        command.Parameters.AddWithValue("installation", NpgsqlDbType.Uuid, identity.InstallationId)
        |> ignore

        command.Parameters.AddWithValue("lineage", NpgsqlDbType.Uuid, identity.LineageId)
        |> ignore

        command.Parameters.AddWithValue("epoch", NpgsqlDbType.Bigint, identity.Epoch)
        |> ignore

        command.Parameters.AddWithValue("handoff", NpgsqlDbType.Uuid, handoffId)
        |> ignore

        command.Parameters.AddWithValue("key", NpgsqlDbType.Uuid, keyId) |> ignore

    let prepare
        ownerWitnessConnection
        (witness: WitnessProtocol)
        (value: WriterHandoffPreparation)
        (canonical: byte array)
        (signature: byte array)
        (oldCapability: byte array)
        =
        let keyId = witness.KeyCustody.ActiveKeyId

        let ciphertext =
            witness.KeyCustody.Encrypt(
                keyId,
                witness.AssociatedData(value.HandoffId, "INTENT"),
                canonical
            )

        try
            use connection = new NpgsqlConnection(ownerWitnessConnection)
            connection.Open()

            use command =
                new NpgsqlCommand(
                    "SELECT sequence,entry_hash,payload_sha256 "
                    + "FROM claimcore_witness.prepare_writer_handoff("
                    + "@installation,@lineage,@epoch,@handoff,@oldGeneration,"
                    + "@expectedSequence,@expectedHash,@newCapability,@checkpointKey,"
                    + "@canonical,@signature,@approvalOne,@approvalTwo,@key,@ciphertext,@oldCapability)",
                    connection
                )

            common command witness.Identity value.HandoffId keyId
            command.Parameters.AddWithValue("oldGeneration", value.OldGeneration) |> ignore

            command.Parameters.AddWithValue("expectedSequence", value.ExpectedTipSequence)
            |> ignore

            command.Parameters.AddWithValue("expectedHash", value.ExpectedTipHash) |> ignore

            command.Parameters.AddWithValue("newCapability", value.NewCapabilitySha256)
            |> ignore

            command.Parameters.AddWithValue("checkpointKey", value.CheckpointSigningKeyId)
            |> ignore

            command.Parameters.AddWithValue("canonical", canonical) |> ignore
            command.Parameters.AddWithValue("signature", signature) |> ignore
            command.Parameters.AddWithValue("approvalOne", value.ApprovalOneId) |> ignore
            command.Parameters.AddWithValue("approvalTwo", value.ApprovalTwoId) |> ignore
            command.Parameters.AddWithValue("ciphertext", ciphertext) |> ignore
            command.Parameters.AddWithValue("oldCapability", oldCapability) |> ignore
            use reader = command.ExecuteReader()
            let result = ticket reader value.HandoffId keyId Intent

            { result with
                Epoch = witness.Identity.Epoch
            }
        finally
            CryptographicOperations.ZeroMemory(ciphertext)

    let commit
        ownerWitnessConnection
        (witness: WitnessProtocol)
        (value: WriterHandoffSettlement)
        (prepareCanonical: byte array)
        (canonical: byte array)
        (signature: byte array)
        (oldCapability: byte array)
        (newCapability: byte array)
        =
        let keyId = witness.KeyCustody.ActiveKeyId
        let digest = WriterHandoffEvidenceHash.settlement prepareCanonical canonical

        let ciphertext =
            witness.KeyCustody.Encrypt(
                keyId,
                witness.AssociatedData(value.HandoffId, "SETTLED_AUTHORITY"),
                digest
            )

        try
            use connection = new NpgsqlConnection(ownerWitnessConnection)
            connection.Open()

            use command =
                new NpgsqlCommand(
                    "SELECT sequence,entry_hash,payload_sha256 "
                    + "FROM claimcore_witness.commit_writer_handoff("
                    + "@installation,@lineage,@epoch,@handoff,@prepareSequence,"
                    + "@prepareHash,@oldCapability,@newCapability,@canonical,@signature,"
                    + "@key,@ciphertext)",
                    connection
                )

            common command witness.Identity value.HandoffId keyId

            command.Parameters.AddWithValue("prepareSequence", value.PrepareSequence)
            |> ignore

            command.Parameters.AddWithValue("prepareHash", value.PrepareHash) |> ignore
            command.Parameters.AddWithValue("oldCapability", oldCapability) |> ignore
            command.Parameters.AddWithValue("newCapability", newCapability) |> ignore
            command.Parameters.AddWithValue("canonical", canonical) |> ignore
            command.Parameters.AddWithValue("signature", signature) |> ignore
            command.Parameters.AddWithValue("ciphertext", ciphertext) |> ignore
            use reader = command.ExecuteReader()
            let result = ticket reader value.HandoffId keyId SettledAuthority

            { result with
                Epoch = witness.Identity.Epoch
            }
        finally
            CryptographicOperations.ZeroMemory(ciphertext)
            CryptographicOperations.ZeroMemory(digest)
