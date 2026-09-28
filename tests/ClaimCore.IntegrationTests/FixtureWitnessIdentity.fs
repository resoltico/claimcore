module internal ClaimCore.IntegrationTests.FixtureWitnessIdentity

open Npgsql
open Expecto
open ClaimCore.Witness

let read (admin: string) : Identity =
    use connection = new NpgsqlConnection(admin)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT installation_id, lineage_id, witness_epoch FROM claimcore.installation_lineage WHERE singleton",
            connection
        )

    use reader = command.ExecuteReader()

    if not (reader.Read()) then
        failtest "Synthetic installation identity is required."

    {
        InstallationId = reader.GetGuid(0)
        LineageId = reader.GetGuid(1)
        Epoch = reader.GetInt64(2)
    }
