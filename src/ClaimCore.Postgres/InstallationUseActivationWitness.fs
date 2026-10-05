namespace ClaimCore.Postgres

open System
open System.Threading
open System.Security.Cryptography
open Npgsql
open NpgsqlTypes
open ClaimCore.Witness

/// Owner-only witness atomic INTENT/settlement plus exact encrypted readback.
module internal InstallationUseActivationWitness =
    let private append
        ownerConnection
        (witness: WitnessProtocol)
        eventId
        expectedSequence
        expectedHash
        (canonical: byte array)
        encryptedIntent
        encryptedSettlement
        (ct: CancellationToken)
        =
        task {
            use connection = new NpgsqlConnection(ownerConnection)
            do! connection.OpenAsync(ct)

            use command =
                new NpgsqlCommand(
                    "SELECT intent_sequence,intent_hash,settlement_sequence,settlement_hash "
                    + "FROM claimcore_witness.activate_data_use(@installation,@lineage,@epoch,"
                    + "@event,@expectedSequence,@expectedHash,@canonical,@key,@intent,@settlement)",
                    connection
                )

            let identity = witness.Identity
            Sql.uuid command "installation" identity.InstallationId
            Sql.uuid command "lineage" identity.LineageId
            Sql.integer command "epoch" identity.Epoch
            Sql.uuid command "event" eventId
            Sql.integer command "expectedSequence" expectedSequence
            Sql.add command "expectedHash" NpgsqlDbType.Bytea (box expectedHash)
            Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
            Sql.uuid command "key" witness.KeyCustody.ActiveKeyId
            Sql.add command "intent" NpgsqlDbType.Bytea (box encryptedIntent)
            Sql.add command "settlement" NpgsqlDbType.Bytea (box encryptedSettlement)
            ct.ThrowIfCancellationRequested()
            use! reader = command.ExecuteReaderAsync(CancellationToken.None)

            let! found = reader.ReadAsync(CancellationToken.None)

            if not found then
                invalidOp "Data-use witness tickets are unavailable."

            let intent = reader.GetInt64(0), reader.GetFieldValue<byte array>(1)
            let settled = reader.GetInt64(2), reader.GetFieldValue<byte array>(3)

            let! duplicated = reader.ReadAsync(CancellationToken.None)

            if duplicated || fst settled <> fst intent + 1L then
                invalidOp "Data-use witness tickets are ambiguous."

            return intent, settled
        }

    let private requireActivated (witness: WitnessProtocol) eventId canonical intent settled =
        task {
            let! _ =
                WriterActivationWitness.verifyHistorical
                    witness
                    eventId
                    canonical
                    intent
                    settled
                    CancellationToken.None

            let! snapshot = witness.Snapshot(CancellationToken.None)

            if
                snapshot.Use.Scope <> InstallationUseScope.RealData
                || snapshot.Use.Phase <> InstallationUsePhase.Active
                || snapshot.Use.ActivationEventId <> Some eventId
                || snapshot.Use.ActivationSequence <> Some(fst settled)
                || snapshot.Use.ActivationHash <> Some(snd settled)
            then
                invalidOp "Witness real-data activation did not settle exactly."

        }

    let activate
        ownerConnection
        (witness: WitnessProtocol)
        eventId
        expectedSequence
        expectedHash
        (canonical: byte array)
        (ct: CancellationToken)
        =
        task {
            let digest = SHA256.HashData(canonical)
            let keyId = witness.KeyCustody.ActiveKeyId

            let encryptedIntent =
                witness.KeyCustody.Encrypt(
                    keyId,
                    witness.AssociatedData(eventId, "INTENT"),
                    canonical
                )

            let encryptedSettlement =
                witness.KeyCustody.Encrypt(
                    keyId,
                    witness.AssociatedData(eventId, "SETTLED_AUTHORITY"),
                    digest
                )

            try
                let! intent, settled =
                    append
                        ownerConnection
                        witness
                        eventId
                        expectedSequence
                        expectedHash
                        canonical
                        encryptedIntent
                        encryptedSettlement
                        ct

                do! requireActivated witness eventId canonical intent settled

                return intent, settled
            finally
                CryptographicOperations.ZeroMemory(digest)
                CryptographicOperations.ZeroMemory(encryptedIntent)
                CryptographicOperations.ZeroMemory(encryptedSettlement)
        }
