module ClaimCore.IntegrationTests.MaintenanceFailureTests

open System
open Expecto
open Npgsql
open ClaimCore.Application.Contracts
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.IntegrationTests.VerificationSupport

let private lostEntry drop landed (database: PostgresFixture.Database) =
    task {
        let settings =
            NpgsqlConnectionStringBuilder(database.Container.GetConnectionString())

        use proxy = new CommitProxy.Proxy(nonNull settings.Host, settings.Port, drop)
        settings.Host <- "127.0.0.1"
        settings.Port <- proxy.Port
        settings.SslMode <- SslMode.Disable
        settings.GssEncryptionMode <- GssEncryptionMode.Disable
        settings.Pooling <- false
        use source = NpgsqlDataSource.Create settings.ConnectionString
        let mutable unknown = false

        try
            let! _ = RegisterMaintenanceStore.change source MaintenanceChange.Enter token
            ()
        with :? AdministrationOutcomeUnknownException ->
            unknown <- true

        Expect.isTrue unknown "Lost commit acknowledgement remains an unknown owner result"
        let! observed = InstallationStatus.read database.OwnerSource token

        Expect.equal
            observed.Maintenance.Active
            landed
            "A fresh owner status establishes the actual state"

        Expect.equal
            observed.Maintenance.Generation
            (if landed then 1L else 0L)
            "Status, not blind reversal, establishes generation"

        Expect.equal
            observed.Position.Sequence
            ClaimCore.Domain.Sequence.zero
            "Operational uncertainty never invents a ledger event"
    }

[<Tests>]
let tests =
    testList
        "maintenance acknowledgement"
        [
            testCaseAsync
                "before-commit loss requires explicit status inspection"
                (run (lostEntry CommitProxy.Drop.BeforeCommit false))
            testCaseAsync
                "after-commit loss retains active maintenance and its generation"
                (run (lostEntry CommitProxy.Drop.AfterCommit true))
        ]
