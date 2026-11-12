module ClaimCore.IntegrationTests.ExpiryFrontierPostgresTests

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Postgres
open ClaimCore.TestSupport

let private token = CancellationToken.None
let private sync (pending: System.Threading.Tasks.Task<'a>) = pending.GetAwaiter().GetResult()

let private storage () =
    let database = PostgresFixture.create () |> sync

    try
        let read () =
            DatabaseSession.read database.RuntimeSource token (fun connection ->
                ExpiryFrontierStorage.read connection token)
            |> sync

        let candidate = DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero).AddTicks(1)
        Expect.isNone (read ()) "Fresh installation confirms no expiry"
        use connection = database.RuntimeSource.OpenConnection()

        Expect.equal
            (ExpiryFrontierStorage.advance connection candidate token |> sync)
            candidate
            "No microsecond rounding"

        Expect.equal (read ()) (Some candidate) "Durable full ticks"

        Expect.equal
            (ExpiryFrontierStorage.advance connection (candidate.AddDays -1) token |> sync)
            candidate
            "Rollback cannot lower cutoff"

        let refuse source code statement parameters =
            use connection = (source: NpgsqlDataSource).OpenConnection()

            try
                DatabaseSession.execute connection statement parameters token |> sync |> ignore
                failtest "Protected frontier mutation accepted"
            with :? PostgresException as error ->
                Expect.equal error.SqlState code "Exact refusal"

            Expect.equal (read ()) (Some candidate) "Refusal preserves prior committed frontier"

        refuse
            database.RuntimeSource
            "42501"
            "UPDATE claimcore.ledger_head SET expired_before_ticks=NULL"
            [||]

        refuse
            database.OwnerSource
            "CC005"
            "UPDATE claimcore.ledger_head SET expired_before_ticks=NULL"
            [||]

        refuse
            database.OwnerSource
            "CC005"
            "UPDATE claimcore.ledger_head SET expired_before_ticks=$1"
            [| box (candidate.UtcTicks - 1L) |]

        refuse
            database.RuntimeSource
            "CC005"
            "SELECT claimcore.advance_expiry_frontier($1)"
            [| box -1L |]

        use transaction = connection.BeginTransaction()
        let later = candidate.AddTicks 1L

        Expect.equal
            (ExpiryFrontierStorage.advance connection later token |> sync)
            later
            "In-transaction frontier advances"

        Expect.equal
            (ExpiryFrontierStorage.read connection token |> sync)
            (Some later)
            "Same transaction sees its advance"

        transaction.Rollback()
        Expect.equal (read ()) (Some candidate) "Rollback publishes no final expiry"
    finally
        database.Fixture.Dispose()

[<Tests>]
let tests =
    testCase
        "PostgreSQL expiry frontier preserves precision, monotonicity and transaction boundaries"
        storage
