namespace ClaimCore.Postgres

open System
open System.Threading
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

    let private connect connectionString (ct: CancellationToken) =
        task {
            let connection = new NpgsqlConnection(connectionString)

            try
                do! connection.OpenAsync(ct)
                return connection
            with error ->
                connection.Dispose()
                return raise error
        }

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
        (ct: CancellationToken)
        =
        task {
            let! evidence =
                witness.EvidenceStore.TryReadEvidence(value.HandoffId, AbortedBeforeCommit, ct)

            match evidence with
            | None ->
                return
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

                return Array.copy stored.EncryptedPayload
        }

    let private readAbortTicket
        (reader: System.Data.Common.DbDataReader)
        (witness: WitnessProtocol)
        (value: WriterHandoffAbort)
        keyId
        (ct: CancellationToken)
        =
        task {
            let! found = reader.ReadAsync(ct)

            if not found then
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

            let! duplicated = reader.ReadAsync(ct)

            if duplicated then
                invalidOp "Witness abort ticket is duplicated."

            return ticket
        }

    let abort
        ownerWitnessConnection
        (witness: WitnessProtocol)
        (value: WriterHandoffAbort)
        canonical
        signatureOne
        signatureTwo
        oldCapability
        (ct: CancellationToken)
        =
        task {
            let keyId = witness.KeyCustody.ActiveKeyId
            let digest = candidate canonical signatureOne signatureTwo

            let! ciphertext = exactCiphertext witness value keyId digest ct

            try
                use! connection = connect ownerWitnessConnection ct

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
                ct.ThrowIfCancellationRequested()
                use! reader = command.ExecuteReaderAsync(CancellationToken.None)
                return! readAbortTicket reader witness value keyId CancellationToken.None
            finally
                CryptographicOperations.ZeroMemory(digest)
                CryptographicOperations.ZeroMemory(ciphertext)
        }

    let release
        ownerWitnessConnection
        (witness: WitnessProtocol)
        handoffId
        (abortTicket: Ticket)
        oldCapability
        (ct: CancellationToken)
        =
        task {
            use! connection = connect ownerWitnessConnection ct

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

            ct.ThrowIfCancellationRequested()
            let! result = command.ExecuteScalarAsync(CancellationToken.None)

            match result with
            | :? bool as doneValue when doneValue -> return ()
            | _ -> return invalidOp "Witness abort release was not confirmed."
        }
