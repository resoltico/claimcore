module ClaimCore.IntegrationTests.BaselineRefusalTests

open ClaimCore.Contract

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Postgres
open ClaimCore.Application

let private token = CancellationToken.None

let private dump (database: Testcontainers.PostgreSql.PostgreSqlContainer) =
    task {
        let! result =
            database.ExecAsync
                [|
                    "pg_dump"
                    "-U"
                    "postgres"
                    "--restrict-key=ClaimCoreSyntheticFixture"
                    "postgres"
                |]

        Expect.equal result.ExitCode (Nullable 0L) "Owned synthetic installation can be inspected"
        return result.Stdout |> Encoding.UTF8.GetBytes |> SHA256.HashData
    }

let private install (source: NpgsqlDataSource) =
    task {
        let bytes =
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "0001_baseline.v0.11.0.sql"))

        use! connection = source.OpenConnectionAsync()
        use! transaction = connection.BeginTransactionAsync()

        let! _ =
            DatabaseSession.execute
                connection
                "CREATE ROLE claimcore_runtime LOGIN PASSWORD 'synthetic-only'"
                [||]
                token

        let! _ =
            DatabaseSession.execute connection (Encoding.UTF8.GetString bytes) [||] token

        let! _ =
            DatabaseSession.execute
                connection
                "INSERT INTO claimcore.schema_migrations(version,name,script_sha256) VALUES (1,'baseline',$1)"
                [| box (SHA256.HashData bytes) |]
                token

        let! _ =
            DatabaseSession.execute
                connection
                "INSERT INTO claimcore.installation(installation_id,business_time_zone) VALUES ('10000000-0000-4000-8000-000000000001','Etc/UTC')"
                [||]
                token

        do! transaction.CommitAsync()
    }

let private refuses action =
    task {
        let mutable refused = false

        try
            do! action ()
        with :? InvalidOperationException ->
            refused <- true

        Expect.isTrue refused "Published baseline bytes are refused by the baseline admission"
    }

let private previousInstallation () =
    async {
        let database = PostgresFixture.empty ()

        try
            do! database.StartAsync() |> Async.AwaitTask
            use source = NpgsqlDataSource.Create(database.GetConnectionString())
            do! install source |> Async.AwaitTask
            let! before = dump database |> Async.AwaitTask

            do!
                refuses (fun () ->
                    task { let! _ = Baseline.initialize source "Etc/UTC" token in return () })
                |> Async.AwaitTask

            let settings = NpgsqlConnectionStringBuilder(database.GetConnectionString())
            settings.Username <- "claimcore_runtime"
            settings.Password <- "synthetic-only"

            let! admission =
                Runtime.openConnection settings.ConnectionString token |> Async.AwaitTask

            match admission with
            | Error RuntimeOpenFault.SchemaMismatch -> ()
            | Error _ -> failtest "The old checksum must cause a schema refusal."
            | Ok runtime ->
                runtime.Dispose()
                failtest "The published baseline was admitted."

            let! after = dump database |> Async.AwaitTask

            Expect.equal
                after
                before
                "Schema, metadata and contents remain untouched after administration and runtime refusals"
        finally
            database.DisposeAsync().AsTask().GetAwaiter().GetResult()
    }

[<Tests>]
let tests =
    testCaseAsync
        "[CC-DB-001] installation using the published 0.11.0 baseline is refused untouched"
        (previousInstallation ())
