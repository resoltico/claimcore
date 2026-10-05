module ClaimCore.IntegrationTests.RestoreProduceAuditTests

open System
open Expecto
open Npgsql
open ClaimCore.Database
open ClaimCore.HostSecurity
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures

let private databaseFrom (source: string) (target: string) =
    let selected = NpgsqlConnectionStringBuilder(source)
    let baseConnection = NpgsqlConnectionStringBuilder(target)
    baseConnection.Database <- selected.Database
    baseConnection.ConnectionString

let private inspect owner _ writer (witness: ClaimCore.Postgres.WitnessProtocol) =
    let principal = human "restore-audit-owner"
    provision owner witness principal |> applied
    let auditor = databaseFrom writer (witnessAuditConnection ())
    let witnessOwner = databaseFrom writer (witnessOwnerConnection ())
    let keyId = witness.KeyCustody.ActiveKeyId
    let material = witnessKey ()

    try
        use custody = new KeyRing(keyId, [ keyId, material ]) :> IKeyCustody
        use suppression = SuppressionKeyFile.Load(suppressionKeyFile ())

        let summary, tip, facts =
            DatabaseVerifyData.auditedRestoredWith
                owner
                auditor
                custody
                suppression
                (fun connection transaction proof audit snapshot ->
                    DatabaseRestoreLive.inspect
                        connection
                        transaction
                        witnessOwner
                        proof
                        audit
                        snapshot)
            |> await

        Expect.equal summary.PendingIntents 0L "Read-only restored-pair audit has no pending work"
        Expect.equal facts.WitnessCutoff tip.TipSequence "Audited pair binds one exact cutoff"

        Expect.notEqual
            facts.PrimarySystemId
            facts.WitnessSystemId
            "Two live PostgreSQL clusters have different physical identities"
    finally
        Array.Clear(material)

let tests =
    testList
        "restore producer audit role"
        [
            testCase
                "[CC-BACKUP-001] restored-pair producer audits two clusters without writer capability"
                (fun _ -> withAuthorityRuntimeDatabase inspect)
        ]
