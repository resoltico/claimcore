namespace ClaimCore.Witness

open System
open Npgsql
open NpgsqlTypes

[<NoEquality; NoComparison>]
type internal WitnessDataUseActivation =
    {
        EventId: Guid
        Canonical: byte array
        IntentSequence: int64
        IntentHash: byte array
        SettlementSequence: int64
        SettlementHash: byte array
        WriterGeneration: int64
    }

/// A read-only historical ticket for exact primary repair, not fresh activation authority.
module internal WitnessDataUseActivation =
    let read writerConnection (identity: Identity) =
        use connection = PostgresTransport.connection writerConnection
        connection.Open()
        WitnessStoreRead.checkAdmission identity connection

        use command =
            new NpgsqlCommand(
                "SELECT data_use_scope,data_use_phase,data_use_activation_event_id,"
                + "data_use_activation_canonical,data_use_activation_intent_sequence,"
                + "data_use_activation_intent_hash,data_use_activation_sequence,"
                + "data_use_activation_hash,writer_generation "
                + "FROM claimcore_witness.installation WHERE singleton "
                + "AND installation_id=@installation AND lineage_id=@lineage AND epoch=@epoch",
                connection
            )

        command.Parameters.AddWithValue("installation", NpgsqlDbType.Uuid, identity.InstallationId)
        |> ignore

        command.Parameters.AddWithValue("lineage", NpgsqlDbType.Uuid, identity.LineageId)
        |> ignore

        command.Parameters.AddWithValue("epoch", NpgsqlDbType.Bigint, identity.Epoch)
        |> ignore

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Witness data-use state is unavailable."

        let scope = InstallationUse.parseScope (reader.GetString(0))
        let phase = InstallationUse.parsePhase (reader.GetString(1))

        let result =
            if scope = InstallationUseScope.RealData && phase = InstallationUsePhase.Active then
                Some
                    {
                        EventId = reader.GetGuid(2)
                        Canonical = reader.GetFieldValue<byte array>(3)
                        IntentSequence = reader.GetInt64(4)
                        IntentHash = reader.GetFieldValue<byte array>(5)
                        SettlementSequence = reader.GetInt64(6)
                        SettlementHash = reader.GetFieldValue<byte array>(7)
                        WriterGeneration = reader.GetInt64(8)
                    }
            else
                None

        if reader.Read() then
            invalidOp "Witness data-use state is ambiguous."

        result
