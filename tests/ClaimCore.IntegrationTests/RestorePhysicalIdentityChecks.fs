module internal ClaimCore.IntegrationTests.RestorePhysicalIdentityChecks

open System
open Expecto
open Npgsql
open ClaimCore.Database
open ClaimCore.Witness
open ClaimCore.IntegrationTests.RestorePhysicalConnections

let requireWitnessIdentity
    (access: RestoredPairAccess)
    (facts: RestoredPairFacts)
    (captured: Snapshot)
    =
    use restoredWitness = new NpgsqlConnection(access.WitnessOwner)
    restoredWitness.Open()

    use identityQuery =
        new NpgsqlCommand(
            "SELECT installation_id::text FROM claimcore_witness.installation WHERE singleton",
            restoredWitness
        )

    let restoredInstallation =
        match identityQuery.ExecuteScalar() with
        | :? string as value -> value
        | _ -> failtest "Physical witness BASE installation is unavailable"

    Expect.equal
        restoredInstallation
        (facts.InstallationId.ToString("D"))
        "Physical witness BASE retained the captured installation"

    Expect.equal
        facts.InstallationId
        captured.Identity.InstallationId
        "Source and restored installation identities diverged"

    Expect.equal
        facts.LineageId
        captured.Identity.LineageId
        "Source and restored lineage identities diverged"

    Expect.notEqual
        facts.PrimarySystemId
        facts.WitnessSystemId
        "Restored systems are not independent"
