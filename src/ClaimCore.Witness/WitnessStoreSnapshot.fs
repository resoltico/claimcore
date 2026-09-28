namespace ClaimCore.Witness

open Npgsql
open NpgsqlTypes

/// One bounded read of the independently committed witness authority projection.
module internal WitnessStoreSnapshot =
    let private optional (reader: System.Data.Common.DbDataReader) index read =
        if reader.IsDBNull(index) then None else Some(read index)

    let private query (connection: NpgsqlConnection) (identity: Identity) =
        let command =
            new NpgsqlCommand(
                "SELECT initial_key_id,active_key_id,writer_generation,handoff_pending,activation_pending,"
                + "activation_event_id,activation_sequence,activation_hash,tip_sequence,tip_hash,"
                + "last_aborted_handoff_id,last_aborted_handoff_sequence,last_aborted_handoff_hash,"
                + "data_use_scope,data_use_phase,data_use_activation_event_id,"
                + "data_use_activation_sequence,data_use_activation_hash "
                + "FROM claimcore_witness.installation "
                + "WHERE singleton AND installation_id=@installation AND lineage_id=@lineage AND epoch=@epoch",
                connection
            )

        command.Parameters.AddWithValue("installation", NpgsqlDbType.Uuid, identity.InstallationId)
        |> ignore

        command.Parameters.AddWithValue("lineage", NpgsqlDbType.Uuid, identity.LineageId)
        |> ignore

        command.Parameters.AddWithValue("epoch", NpgsqlDbType.Bigint, identity.Epoch)
        |> ignore

        command

    let read writerConnection (identity: Identity) =
        use connection = PostgresTransport.connection writerConnection
        connection.Open()
        WitnessStoreRead.checkAdmission identity connection

        use command = query connection identity

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Witness tip is missing."

        let result =
            {
                Identity = identity
                Use =
                    {
                        Scope = InstallationUse.parseScope (reader.GetString(13))
                        Phase = InstallationUse.parsePhase (reader.GetString(14))
                        ActivationEventId = optional reader 15 reader.GetGuid
                        ActivationSequence = optional reader 16 reader.GetInt64
                        ActivationHash = optional reader 17 reader.GetFieldValue<byte array>
                    }
                InitialKeyId = reader.GetGuid(0)
                ActiveKeyId = reader.GetGuid(1)
                WriterGeneration = reader.GetInt64(2)
                HandoffPending = reader.GetBoolean(3)
                ActivationPending = reader.GetBoolean(4)
                ActivationEventId = optional reader 5 reader.GetGuid
                ActivationSequence = optional reader 6 reader.GetInt64
                ActivationHash = optional reader 7 reader.GetFieldValue<byte array>
                TipSequence = reader.GetInt64(8)
                TipHash = reader.GetFieldValue<byte array>(9)
                LastAbortedHandoffId = optional reader 10 reader.GetGuid
                LastAbortedHandoffSequence = optional reader 11 reader.GetInt64
                LastAbortedHandoffHash = optional reader 12 reader.GetFieldValue<byte array>
            }

        if reader.Read() then
            invalidOp "Witness tip is duplicated."

        result
