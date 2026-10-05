namespace ClaimCore.Witness

open System
open System.Threading
open Npgsql
open NpgsqlTypes

/// Active key marker and the bounded retained key set required by witness custody.
module internal WitnessStoreKeyRequirements =
    let readCheck writerConnection (identity: Identity) (ct: CancellationToken) =
        task {
            use connection = PostgresTransport.connection writerConnection
            do! connection.OpenAsync(ct)
            do! WitnessDatabaseAdmission.checkAsync identity connection ct

            use command =
                new NpgsqlCommand(
                    "SELECT active_key_id,key_check_envelope FROM claimcore_witness.installation "
                    + "WHERE singleton AND installation_id=@installation AND lineage_id=@lineage",
                    connection
                )

            command.Parameters.AddWithValue(
                "installation",
                NpgsqlDbType.Uuid,
                identity.InstallationId
            )
            |> ignore

            command.Parameters.AddWithValue("lineage", NpgsqlDbType.Uuid, identity.LineageId)
            |> ignore

            use! reader = command.ExecuteReaderAsync(ct)

            let! found = reader.ReadAsync(ct)

            if not found then
                invalidOp "Witness key marker is missing."

            let result = reader.GetGuid(0), reader.GetFieldValue<byte array>(1)

            let! duplicated = reader.ReadAsync(ct)

            if duplicated then
                invalidOp "Witness key marker is duplicated."

            return result
        }

    let requiredIds writerConnection (identity: Identity) (ct: CancellationToken) =
        task {
            use connection = PostgresTransport.connection writerConnection
            do! connection.OpenAsync(ct)
            do! WitnessDatabaseAdmission.checkAsync identity connection ct

            use command =
                new NpgsqlCommand(
                    "SELECT DISTINCT key_id FROM claimcore_witness.journal "
                    + "WHERE installation_id=@installation LIMIT 65",
                    connection
                )

            command.Parameters.AddWithValue(
                "installation",
                NpgsqlDbType.Uuid,
                identity.InstallationId
            )
            |> ignore

            use! reader = command.ExecuteReaderAsync(ct)
            let ids = ResizeArray<Guid>()

            while! reader.ReadAsync(ct) do
                ids.Add(reader.GetGuid(0))

            if ids.Count > 64 then
                invalidOp "Witness key rotation limit is exceeded."

            return ids |> Seq.toList
        }
