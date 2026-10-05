namespace ClaimCore.Witness

open System.Threading
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
                + "data_use_activation_sequence,data_use_activation_hash,"
                + "loss_retirement_pending,loss_retired,loss_retirement_id,"
                + "loss_retirement_intent_sequence,loss_retirement_intent_hash,"
                + "loss_retirement_sequence,loss_retirement_hash "
                + "FROM claimcore_witness.installation "
                + "WHERE singleton AND installation_id=@installation AND lineage_id=@lineage AND epoch=@epoch",
                connection
            )

        WitnessDatabaseAdmission.bindIdentity command identity

        command

    let read writerConnection (identity: Identity) (ct: CancellationToken) =
        task {
            use connection = PostgresTransport.connection writerConnection
            do! connection.OpenAsync(ct)
            do! WitnessDatabaseAdmission.checkAsync identity connection ct

            use command = query connection identity

            use! reader = command.ExecuteReaderAsync(ct)

            let! found = reader.ReadAsync(ct)

            if not found then
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
                    LossRetirementPending = reader.GetBoolean(18)
                    LossRetired = reader.GetBoolean(19)
                    LossRetirementId = optional reader 20 reader.GetGuid
                    LossRetirementIntentSequence = optional reader 21 reader.GetInt64
                    LossRetirementIntentHash = optional reader 22 reader.GetFieldValue<byte array>
                    LossRetirementSequence = optional reader 23 reader.GetInt64
                    LossRetirementHash = optional reader 24 reader.GetFieldValue<byte array>
                    ActivationEventId = optional reader 5 reader.GetGuid
                    ActivationSequence = optional reader 6 reader.GetInt64
                    ActivationHash = optional reader 7 reader.GetFieldValue<byte array>
                    TipSequence = reader.GetInt64(8)
                    TipHash = reader.GetFieldValue<byte array>(9)
                    LastAbortedHandoffId = optional reader 10 reader.GetGuid
                    LastAbortedHandoffSequence = optional reader 11 reader.GetInt64
                    LastAbortedHandoffHash = optional reader 12 reader.GetFieldValue<byte array>
                }

            let! duplicated = reader.ReadAsync(ct)

            if duplicated then
                invalidOp "Witness tip is duplicated."

            return result
        }
