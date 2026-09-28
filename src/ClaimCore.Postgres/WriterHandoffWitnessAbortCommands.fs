namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Text
open Npgsql
open NpgsqlTypes
open ClaimCore.Witness

/// Narrow schema-owner witness abort: A1 retains pending; A3 releases only after primary A2.
module internal WriterHandoffWitnessAbortCommands =
    let candidate canonical signatureOne signatureTwo =
        SHA256.HashData(
            Array.concat
                [
                    Encoding.ASCII.GetBytes("CLAIMCORE_WRITER_HANDOFF_ABORT_V1:")
                    canonical
                    signatureOne
                    signatureTwo
                ]
        )

    let private connect connectionString =
        let connection = new NpgsqlConnection(connectionString)
        connection.Open()
        connection

    let private identity (command: NpgsqlCommand) (witness: WitnessProtocol) handoffId =
        Sql.uuid command "installation" witness.Identity.InstallationId
        Sql.uuid command "lineage" witness.Identity.LineageId
        Sql.integer command "epoch" witness.Identity.Epoch
        Sql.uuid command "handoff" handoffId

    let private exactCiphertext
        (witness: WitnessProtocol)
        (value: WriterHandoffAbort)
        keyId
        (digest: byte array)
        =
        match witness.EvidenceStore.TryReadEvidence(value.HandoffId, AbortedBeforeCommit) with
        | None ->
            witness.KeyCustody.Encrypt(
                keyId,
                witness.AssociatedData(value.HandoffId, "ABORTED_BEFORE_COMMIT"),
                digest
            )
        | Some stored ->
            if
                stored.Ticket.OperationId <> value.HandoffId
                || stored.Ticket.Phase <> AbortedBeforeCommit
                || stored.Ticket.ScopeKind <> Installation
                || stored.Ticket.SubjectCaseId.IsSome
                || stored.Ticket.KeyId <> keyId
                || stored.Ticket.PayloadHash <> SHA256.HashData(stored.EncryptedPayload)
            then
                invalidOp "Stored abort ciphertext identity differs."

            let plain =
                witness.KeyCustody.Decrypt(
                    keyId,
                    witness.AssociatedData(value.HandoffId, "ABORTED_BEFORE_COMMIT"),
                    stored.EncryptedPayload
                )

            try
                if plain <> digest then
                    invalidOp "Stored abort ciphertext candidate differs."
            finally
                CryptographicOperations.ZeroMemory(plain)

            Array.copy stored.EncryptedPayload

    let private readAbortTicket
        (reader: System.Data.Common.DbDataReader)
        (witness: WitnessProtocol)
        (value: WriterHandoffAbort)
        keyId
        =
        if not (reader.Read()) then
            invalidOp "Witness abort ticket is absent."

        let ticket =
            {
                Sequence = reader.GetInt64(0)
                Epoch = witness.Identity.Epoch
                KeyId = keyId
                EntryHash = reader.GetFieldValue<byte array>(1)
                PayloadHash = reader.GetFieldValue<byte array>(2)
                OperationId = value.HandoffId
                Phase = AbortedBeforeCommit
                ScopeKind = Installation
                SubjectCaseId = None
            }

        if reader.Read() then
            invalidOp "Witness abort ticket is duplicated."

        ticket

    let abort
        ownerWitnessConnection
        (witness: WitnessProtocol)
        (value: WriterHandoffAbort)
        canonical
        signatureOne
        signatureTwo
        oldCapability
        =
        let keyId = witness.KeyCustody.ActiveKeyId
        let digest = candidate canonical signatureOne signatureTwo

        let ciphertext = exactCiphertext witness value keyId digest

        try
            use connection = connect ownerWitnessConnection

            use command =
                new NpgsqlCommand(
                    "SELECT sequence,entry_hash,payload_sha256 "
                    + "FROM claimcore_witness.abort_writer_handoff("
                    + "@installation,@lineage,@epoch,@handoff,@prepareSequence,@prepareHash,"
                    + "@oldCapability,@canonical,@signatureOne,@signatureTwo,@keyOne,@keyTwo,"
                    + "@key,@ciphertext)",
                    connection
                )

            identity command witness value.HandoffId
            Sql.integer command "prepareSequence" value.PrepareSequence
            Sql.add command "prepareHash" NpgsqlDbType.Bytea (box value.PrepareHash)
            Sql.add command "oldCapability" NpgsqlDbType.Bytea (box oldCapability)
            Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
            Sql.add command "signatureOne" NpgsqlDbType.Bytea (box signatureOne)
            Sql.add command "signatureTwo" NpgsqlDbType.Bytea (box signatureTwo)
            Sql.uuid command "keyOne" value.AbortSigningKeyOneId
            Sql.uuid command "keyTwo" value.AbortSigningKeyTwoId
            Sql.uuid command "key" keyId
            Sql.add command "ciphertext" NpgsqlDbType.Bytea (box ciphertext)
            use reader = command.ExecuteReader()
            readAbortTicket reader witness value keyId
        finally
            CryptographicOperations.ZeroMemory(digest)
            CryptographicOperations.ZeroMemory(ciphertext)

    let release
        ownerWitnessConnection
        (witness: WitnessProtocol)
        handoffId
        (abortTicket: Ticket)
        oldCapability
        =
        use connection = connect ownerWitnessConnection

        use command =
            new NpgsqlCommand(
                "SELECT claimcore_witness.release_aborted_writer_handoff("
                + "@installation,@lineage,@epoch,@handoff,@abortSequence,@abortHash,@oldCapability)",
                connection
            )

        identity command witness handoffId
        Sql.integer command "abortSequence" abortTicket.Sequence
        Sql.add command "abortHash" NpgsqlDbType.Bytea (box abortTicket.EntryHash)
        Sql.add command "oldCapability" NpgsqlDbType.Bytea (box oldCapability)

        match command.ExecuteScalar() with
        | :? bool as doneValue when doneValue -> ()
        | _ -> invalidOp "Witness abort release was not confirmed."
