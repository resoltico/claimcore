namespace ClaimCore.Witness

open System
open Npgsql
open NpgsqlTypes

/// Auditor-only exact projection of the witness-retained terminal decision.
module internal WitnessStoreLossRetirement =
    let private decode (reader: System.Data.Common.DbDataReader) =
        {
            RetirementId = reader.GetGuid(0)
            InstallationId = reader.GetGuid(1)
            LineageId = reader.GetGuid(2)
            Epoch = reader.GetInt64(3)
            PreviousSequence = reader.GetInt64(4)
            PreviousHash = reader.GetFieldValue<byte array>(5)
            Canonical = reader.GetFieldValue<byte array>(6)
            CanonicalSha256 = reader.GetFieldValue<byte array>(7)
            SignatureOne = reader.GetFieldValue<byte array>(8)
            SignatureTwo = reader.GetFieldValue<byte array>(9)
            SignerOneId = reader.GetGuid(10)
            SignerTwoId = reader.GetGuid(11)
            OwnerOneActorId = reader.GetGuid(12)
            OwnerTwoActorId = reader.GetGuid(13)
            OperationSetKind = reader.GetString(14)
            KnownOperationCount = reader.GetInt32(15)
            KnownOperationDigest = reader.GetFieldValue<byte array>(16)
            IntentSequence = reader.GetInt64(17)
            IntentHash = reader.GetFieldValue<byte array>(18)
            SettlementSequence =
                if reader.IsDBNull(19) then
                    None
                else
                    Some(reader.GetInt64(19))
            SettlementHash =
                if reader.IsDBNull(20) then
                    None
                else
                    Some(reader.GetFieldValue<byte array>(20))
        }

    let read connectionString (identity: Identity) retirementId =
        use connection = PostgresTransport.connection connectionString
        connection.Open()
        WitnessStoreRead.checkAdmission identity connection

        use count =
            new NpgsqlCommand(
                "SELECT count(*) FROM claimcore_witness.installation_loss_retirements",
                connection
            )

        if count.ExecuteScalar() :?> int64 > 1L then
            invalidOp "Witness loss retirement is not unique."

        use command =
            new NpgsqlCommand(
                "SELECT retirement_id,installation_id,lineage_id,old_epoch,previous_sequence,"
                + "previous_hash,canonical_decision,canonical_sha256,signature_one,signature_two,"
                + "signer_one_id,signer_two_id,owner_one_actor_id,owner_two_actor_id,"
                + "operation_set_kind,known_operation_count,known_operation_digest,"
                + "intent_sequence,intent_hash,settlement_sequence,settlement_hash "
                + "FROM claimcore_witness.installation_loss_retirements "
                + "WHERE retirement_id=@retirement",
                connection
            )

        command.Parameters.AddWithValue("retirement", NpgsqlDbType.Uuid, retirementId)
        |> ignore

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            None
        else
            let value = decode reader

            if reader.Read() then
                invalidOp "Witness loss retirement is duplicated."

            Some value
