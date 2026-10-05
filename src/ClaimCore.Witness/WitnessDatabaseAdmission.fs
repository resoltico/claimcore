namespace ClaimCore.Witness

open System
open System.Threading
open Npgsql
open NpgsqlTypes

/// Catalog, role and lineage admission shared by witness read and write connections.
module internal WitnessDatabaseAdmission =
    let bindIdentity (command: NpgsqlCommand) (identity: Identity) =
        command.Parameters.AddWithValue("installation", NpgsqlDbType.Uuid, identity.InstallationId)
        |> ignore

        command.Parameters.AddWithValue("lineage", NpgsqlDbType.Uuid, identity.LineageId)
        |> ignore

        command.Parameters.AddWithValue("epoch", NpgsqlDbType.Bigint, identity.Epoch)
        |> ignore

    let private baselineDigest = lazy (snd (Baseline.script ()))
    let private catalogScript = lazy (Baseline.catalogScript ())
    let private catalogDigest = lazy (Baseline.catalogDigest ())

    let private witnessSchema = "claimcore_witness"

    let private requireStructureAsync role (connection: NpgsqlConnection) (ct: CancellationToken) =
        task {
            use catalog = new NpgsqlCommand(catalogScript.Value, connection)
            let! result = catalog.ExecuteScalarAsync(ct)

            if not (result :? string) || unbox<string> result <> catalogDigest.Value then
                invalidOp "Witness live catalog differs from the frozen manifest."

            let admission =
                match role with
                | "claimcore_witness_writer" -> WitnessAdmission.script ()
                | "claimcore_witness_auditor" -> WitnessAuditAdmission.script ()
                | _ -> invalidOp "Witness role is not admitted."

            use command = new NpgsqlCommand(admission.Structure, connection)
            let! valid = command.ExecuteScalarAsync(ct)

            if not (unbox<bool> valid) then
                invalidOp "Witness database admission failed."
        }

    let checkAsync (identity: Identity) (connection: NpgsqlConnection) (ct: CancellationToken) =
        task {
            use! roleCommand = PreparedCommand.createAsync connection "SELECT current_user" ct
            let! result = roleCommand.ExecuteScalarAsync(ct)

            let role =
                match result with
                | :? string as value -> value
                | _ -> ""

            do!
                CatalogEpoch.admitAsync "witness" witnessSchema connection ct (fun () ->
                    requireStructureAsync role connection ct)

            let admission =
                match role with
                | "claimcore_witness_writer" -> WitnessAdmission.script ()
                | _ -> WitnessAuditAdmission.script ()

            use command = new NpgsqlCommand(admission.Liveness, connection)

            command.Parameters.AddWithValue(
                "installation",
                NpgsqlDbType.Uuid,
                identity.InstallationId
            )
            |> ignore

            command.Parameters.AddWithValue("lineage", NpgsqlDbType.Uuid, identity.LineageId)
            |> ignore

            command.Parameters.AddWithValue("epoch", NpgsqlDbType.Bigint, identity.Epoch)
            |> ignore

            command.Parameters.AddWithValue("digest", NpgsqlDbType.Text, baselineDigest.Value)
            |> ignore

            do! command.PrepareAsync(ct)
            let! valid = command.ExecuteScalarAsync(ct)

            if not (unbox<bool> valid) then
                invalidOp "Witness database admission failed."
        }
