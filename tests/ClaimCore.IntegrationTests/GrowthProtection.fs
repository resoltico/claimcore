module internal ClaimCore.IntegrationTests.GrowthProtection

open System
open System.IO
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Web
open Microsoft.AspNetCore.DataProtection

let private keyRing () =
    let path =
        Path.Combine(
            ClaimCore.TestSupport.RepositoryRoot.find (),
            "artifacts/verification/growth",
            "keys-" + Guid.NewGuid().ToString("N")
        )

    Directory.CreateDirectory(
        path,
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
    )
    |> ignore

    let owned =
        { new IDisposable with
            member _.Dispose() = Directory.Delete(path, true)
        }

    try
        SessionKeys.admit path, owned
    with error ->
        ClaimCore.TestSupport.FixtureDisposal.afterFailure error [ owned.Dispose ]
        reraise ()

let store directory (database: PostgresFixture.Database) =
    let protection = ClaimCore.Web.RecoveryProtection.create directory
    protection.Prepare()

    LedgerStore(
        database.RuntimeSource,
        database.Fixture.InstallationId,
        Transactions.timeouts,
        fun () -> Some protection
    )
    :> ILedgerStore

let sessions (directory: DirectoryInfo) =
    DataProtectionProvider.Create(
        SessionKeys.admit directory.FullName,
        fun builder -> builder.SetApplicationName(SessionKeys.applicationName) |> ignore
    )

let withKeyRing action =
    task {
        let directory, owned = keyRing ()

        let! outcome =
            task {
                try
                    let! result = action directory
                    return Ok result
                with error ->
                    return Error error
            }

        match outcome with
        | Ok result ->
            owned.Dispose()
            return result
        | Error error ->
            ClaimCore.TestSupport.FixtureDisposal.afterFailure error [ owned.Dispose ]
            return raise error
    }
