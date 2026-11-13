module ClaimCore.IntegrationTests.BaselineRefusalTests

open ClaimCore.CommandLine

open ClaimCore.TestSupport

open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json

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

let private install identifier (bytes: byte array) (source: NpgsqlDataSource) =
    task {
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
                "INSERT INTO claimcore.baseline_identity(identifier,script_sha256) VALUES ($1,$2)"
                [| box identifier; box (SHA256.HashData bytes) |]
                token

        let! _ =
            DatabaseSession.execute
                connection
                "INSERT INTO claimcore.installation(installation_id,business_time_zone,installation_format) VALUES ('10000000-0000-4000-8000-000000000001','Etc/UTC',3)"
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

        Expect.isTrue refused "Altered baseline bytes are refused by the baseline admission"
    }

let private refusedInstallation identifier load =
    async {
        let database = PostgresFixture.empty ()

        try
            do! database.StartAsync() |> Async.AwaitTask
            use source = NpgsqlDataSource.Create(database.GetConnectionString())
            do! install identifier (load ()) source |> Async.AwaitTask
            let! before = dump database |> Async.AwaitTask

            do!
                refuses (fun () ->
                    task {
                        let! _ =
                            Baseline.initialize
                                source
                                "Etc/UTC"
                                (fun _ _ -> System.Threading.Tasks.Task.FromResult(()))
                                token in

                        return ()
                    })
                |> Async.AwaitTask

            let settings = NpgsqlConnectionStringBuilder(database.GetConnectionString())
            settings.Username <- "claimcore_runtime"
            settings.Password <- "synthetic-only"

            let! admission =
                Runtime.openConnection settings.ConnectionString token |> Async.AwaitTask

            match admission with
            | Error RuntimeOpenFault.SchemaMismatch -> ()
            | Error _ -> failtest "The incompatible baseline must cause a schema refusal."
            | Ok runtime ->
                runtime.Dispose()
                failtest "The altered baseline was admitted."

            let! after = dump database |> Async.AwaitTask

            Expect.equal
                after
                before
                "Schema, metadata and contents remain untouched after administration and runtime refusals"
        finally
            database.DisposeAsync().AsTask().GetAwaiter().GetResult()
    }

let private alteredBaseline () =
    let baseline =
        File.ReadAllBytes(Path.Combine(RepositoryRoot.find (), "db/baseline.sql"))

    Array.append
        baseline
        (Encoding.UTF8.GetBytes
            "\nCOMMENT ON SCHEMA claimcore IS 'Synthetic altered baseline refusal control';\n")

let private differentChecksum () =
    let baseline =
        File.ReadAllBytes(Path.Combine(RepositoryRoot.find (), "db/baseline.sql"))

    Array.append baseline (Encoding.UTF8.GetBytes "\n-- Synthetic checksum control.\n")

[<Tests>]
let tests =
    testList
        "baseline admission preserves incompatible installations"
        [
            testCaseAsync
                "a synthetic baseline differing by one SQL statement is refused untouched"
                (refusedInstallation Baseline.identifier alteredBaseline)
            testCaseAsync
                "the preceding installation identity refuses without mutation"
                (refusedInstallation "claimcore-narratives-reminders" (fun () ->
                    File.ReadAllBytes(Path.Combine(RepositoryRoot.find (), "db/baseline.sql"))))
            testCaseAsync
                "a schema with a different checksum is refused untouched"
                (refusedInstallation Baseline.identifier differentChecksum)
        ]
