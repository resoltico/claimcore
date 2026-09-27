namespace ClaimCore.Postgres

open System
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
        =
        use connection = new NpgsqlConnection(ownerConnection)
        connection.Open()

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
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Data-use witness tickets are unavailable."

        let intent = reader.GetInt64(0), reader.GetFieldValue<byte array>(1)
        let settled = reader.GetInt64(2), reader.GetFieldValue<byte array>(3)

        if reader.Read() || fst settled <> fst intent + 1L then
            invalidOp "Data-use witness tickets are ambiguous."

        intent, settled

    let activate
        ownerConnection
        (witness: WitnessProtocol)
        eventId
        expectedSequence
        expectedHash
        (canonical: byte array)
        =
        let digest = SHA256.HashData(canonical)
        let keyId = witness.KeyCustody.ActiveKeyId

        let encryptedIntent =
            witness.KeyCustody.Encrypt(keyId, witness.AssociatedData(eventId, "INTENT"), canonical)

        let encryptedSettlement =
            witness.KeyCustody.Encrypt(
                keyId,
                witness.AssociatedData(eventId, "SETTLED_AUTHORITY"),
                digest
            )

        try
            let intent, settled =
                append
                    ownerConnection
                    witness
                    eventId
                    expectedSequence
                    expectedHash
                    canonical
                    encryptedIntent
                    encryptedSettlement

            WriterActivationWitness.verifyHistorical witness eventId canonical intent settled
            |> ignore

            let snapshot = witness.Snapshot()

            if
                snapshot.Use.Scope <> InstallationUseScope.RealData
                || snapshot.Use.Phase <> InstallationUsePhase.Active
                || snapshot.Use.ActivationEventId <> Some eventId
                || snapshot.Use.ActivationSequence <> Some(fst settled)
                || snapshot.Use.ActivationHash <> Some(snd settled)
            then
                invalidOp "Witness real-data activation did not settle exactly."

            intent, settled
        finally
            CryptographicOperations.ZeroMemory(digest)
            CryptographicOperations.ZeroMemory(encryptedIntent)
            CryptographicOperations.ZeroMemory(encryptedSettlement)
