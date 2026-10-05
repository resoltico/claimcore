namespace ClaimCore.Witness

open System
open System.Threading
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
    let read writerConnection (identity: Identity) (ct: CancellationToken) =
        task {
            use connection = PostgresTransport.connection writerConnection
            do! connection.OpenAsync(ct)
            do! WitnessDatabaseAdmission.checkAsync identity connection ct

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

            WitnessDatabaseAdmission.bindIdentity command identity

            use! reader = command.ExecuteReaderAsync(ct)

            let! found = reader.ReadAsync(ct)

            if not found then
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

            let! duplicated = reader.ReadAsync(ct)

            if duplicated then
                invalidOp "Witness data-use state is ambiguous."

            return result
        }
