namespace ClaimCore.Postgres

open System
open System.Threading
open System.Security.Cryptography
open System.Text
open Npgsql
open NpgsqlTypes
open ClaimCore.Witness

/// Owner-only W0/W1 adapter. Every definite ticket is independently read back from the
/// auditor connection; a lost response retains the exact candidate and stays uncertain.
module internal InstallationLossRetirementWitness =
    let candidateDigest
        (canonical: byte array)
        (signatureOne: byte array)
        (signatureTwo: byte array)
        (operationDigest: byte array)
        =
        SHA256.HashData(
            Array.concat
                [
                    Encoding.ASCII.GetBytes("CLAIMCORE_INSTALLATION_LOSS_INTENT_V1:")
                    canonical
                    signatureOne
                    signatureTwo
                    operationDigest
                ]
        )

    let settlementDigest (canonical: byte array) (intent: Ticket) =
        SHA256.HashData(
            Array.concat
                [
                    Encoding.ASCII.GetBytes("CLAIMCORE_INSTALLATION_LOSS_SETTLEMENT_V1:")
                    SHA256.HashData(canonical)
                    intent.EntryHash
                ]
        )

    let private ciphertext
        (witness: WitnessProtocol)
        id
        phase
        (plain: byte array)
        (ct: CancellationToken)
        =
        task {
            let keyId = witness.KeyCustody.ActiveKeyId
            let name = ClaimCore.Witness.Encoding.phase phase

            let! retained = witness.EvidenceStore.TryReadEvidence(id, phase, ct)

            match retained with
            | None ->
                return witness.KeyCustody.Encrypt(keyId, witness.AssociatedData(id, name), plain)
            | Some existing ->
                if
                    existing.Ticket.KeyId <> keyId
                    || existing.Ticket.OperationId <> id
                    || existing.Ticket.Phase <> phase
                    || existing.Ticket.ScopeKind <> Installation
                    || existing.Ticket.SubjectCaseId.IsSome
                then
                    invalidOp "Loss witness evidence identity differs."

                let decrypted =
                    witness.KeyCustody.Decrypt(
                        keyId,
                        witness.AssociatedData(id, name),
                        existing.EncryptedPayload
                    )

                try
                    if decrypted <> plain then
                        invalidOp "Loss witness ciphertext differs."

                    return Array.copy existing.EncryptedPayload
                finally
                    CryptographicOperations.ZeroMemory(decrypted)
        }

    let private bindIdentity (command: NpgsqlCommand) (value: InstallationLossRetirementDecision) =
        Sql.uuid command "installation" value.InstallationId
        Sql.uuid command "lineage" value.LineageId
        Sql.integer command "epoch" value.Epoch
        Sql.uuid command "retirement" value.RetirementId

    let private ticket
        (reader: System.Data.Common.DbDataReader)
        (witness: WitnessProtocol)
        (value: InstallationLossRetirementDecision)
        phase
        (ct: CancellationToken)
        =
        task {
            let! found = reader.ReadAsync(ct)

            if not found then
                invalidOp "Loss witness ticket is absent."

            let result =
                {
                    Sequence = reader.GetInt64(0)
                    Epoch = value.Epoch
                    KeyId = witness.KeyCustody.ActiveKeyId
                    EntryHash = reader.GetFieldValue<byte array>(1)
                    PayloadHash = reader.GetFieldValue<byte array>(2)
                    OperationId = value.RetirementId
                    Phase = phase
                    ScopeKind = Installation
                    SubjectCaseId = None
                }

            let! duplicated = reader.ReadAsync(ct)

            if duplicated then
                invalidOp "Loss witness ticket is duplicated."

            return result
        }

    let private exactReadback
        (witness: WitnessProtocol)
        (issued: Ticket)
        plaintext
        (ct: CancellationToken)
        =
        task {
            let! retained =
                witness.EvidenceStore.TryReadEvidence(issued.OperationId, issued.Phase, ct)

            let evidence =
                retained
                |> Option.defaultWith (fun () ->
                    invalidOp "Loss witness committed readback is missing.")

            if evidence.Ticket <> issued then
                invalidOp "Loss witness committed readback differs."

            let name = ClaimCore.Witness.Encoding.phase issued.Phase

            let decoded =
                witness.KeyCustody.Decrypt(
                    issued.KeyId,
                    witness.AssociatedData(issued.OperationId, name),
                    evidence.EncryptedPayload
                )

            try
                if decoded <> plaintext then
                    invalidOp "Loss witness payload readback differs."
            finally
                CryptographicOperations.ZeroMemory(decoded)

            return issued
        }

    let private bindPreparation
        (command: NpgsqlCommand)
        (value: InstallationLossRetirementDecision)
        canonical
        signatureOne
        signatureTwo
        keyId
        encrypted
        =
        bindIdentity command value
        Sql.integer command "previousSequence" value.PreviousSequence
        Sql.add command "previousHash" NpgsqlDbType.Bytea (box value.PreviousHash)
        Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
        Sql.add command "firstSignature" NpgsqlDbType.Bytea (box signatureOne)
        Sql.add command "secondSignature" NpgsqlDbType.Bytea (box signatureTwo)
        Sql.uuid command "firstKey" value.SignerOneId
        Sql.uuid command "secondKey" value.SignerTwoId
        Sql.uuid command "firstOwner" value.OwnerOneActorId
        Sql.uuid command "secondOwner" value.OwnerTwoActorId

        Sql.text
            command
            "setKind"
            (InstallationLossRetirementCandidate.operationSetName value.OperationSet)

        Sql.add command "knownCount" NpgsqlDbType.Integer (box value.KnownOperationCount)
        Sql.add command "knownDigest" NpgsqlDbType.Bytea (box value.KnownOperationDigest)
        Sql.uuid command "key" keyId
        Sql.add command "encrypted" NpgsqlDbType.Bytea (box encrypted)

    let prepare
        ownerWitnessConnection
        (witness: WitnessProtocol)
        (value: InstallationLossRetirementDecision)
        (canonical: byte array)
        (signatureOne: byte array)
        (signatureTwo: byte array)
        (ct: CancellationToken)
        =
        task {
            let plain =
                candidateDigest canonical signatureOne signatureTwo value.KnownOperationDigest

            let! encrypted = ciphertext witness value.RetirementId Intent plain ct

            try
                use connection = new NpgsqlConnection(ownerWitnessConnection)
                do! connection.OpenAsync(ct)

                use command =
                    new NpgsqlCommand(
                        "SELECT sequence,entry_hash,payload_sha256 FROM "
                        + "claimcore_witness.prepare_installation_loss_retirement("
                        + "@installation,@lineage,@epoch,@retirement,@previousSequence,@previousHash,"
                        + "@canonical,@firstSignature,@secondSignature,@firstKey,@secondKey,"
                        + "@firstOwner,@secondOwner,@setKind,@knownCount,@knownDigest,@key,@encrypted)",
                        connection
                    )

                bindPreparation
                    command
                    value
                    canonical
                    signatureOne
                    signatureTwo
                    witness.KeyCustody.ActiveKeyId
                    encrypted

                ct.ThrowIfCancellationRequested()
                use! reader = command.ExecuteReaderAsync(CancellationToken.None)

                let! issued = ticket reader witness value Intent CancellationToken.None
                return! exactReadback witness issued plain CancellationToken.None
            finally
                CryptographicOperations.ZeroMemory(plain)
                CryptographicOperations.ZeroMemory(encrypted)
        }

    let settle
        ownerWitnessConnection
        (witness: WitnessProtocol)
        (value: InstallationLossRetirementDecision)
        (canonical: byte array)
        (intent: Ticket)
        (ct: CancellationToken)
        =
        task {
            let plain = settlementDigest canonical intent
            let! encrypted = ciphertext witness value.RetirementId SettledAuthority plain ct

            try
                use connection = new NpgsqlConnection(ownerWitnessConnection)
                do! connection.OpenAsync(ct)

                use command =
                    new NpgsqlCommand(
                        "SELECT sequence,entry_hash,payload_sha256 FROM "
                        + "claimcore_witness.settle_installation_loss_retirement("
                        + "@installation,@lineage,@epoch,@retirement,@intentSequence,@intentHash,"
                        + "@candidate,@key,@encrypted)",
                        connection
                    )

                bindIdentity command value
                Sql.integer command "intentSequence" intent.Sequence
                Sql.add command "intentHash" NpgsqlDbType.Bytea (box intent.EntryHash)
                Sql.add command "candidate" NpgsqlDbType.Bytea (box (SHA256.HashData(canonical)))
                Sql.uuid command "key" witness.KeyCustody.ActiveKeyId
                Sql.add command "encrypted" NpgsqlDbType.Bytea (box encrypted)
                ct.ThrowIfCancellationRequested()
                use! reader = command.ExecuteReaderAsync(CancellationToken.None)

                let! issued = ticket reader witness value SettledAuthority CancellationToken.None
                return! exactReadback witness issued plain CancellationToken.None
            finally
                CryptographicOperations.ZeroMemory(plain)
                CryptographicOperations.ZeroMemory(encrypted)
        }
