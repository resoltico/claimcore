module ClaimCore.IntegrationTests.SchemaTests

open ClaimCore.CommandLine

open ClaimCore.Json
open ClaimCore.Domain

open ClaimCore.TestSupport

open System
open System.IO
open System.Text
open System.Text.RegularExpressions
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.IntegrationTests.VerificationSupport

let private runFile (database: PostgresFixture.Database) user name =
    task {
        let path = Path.Combine(AppContext.BaseDirectory, name)
        let bytes = File.ReadAllBytes path
        let destination = "/tmp/" + name
        do! database.Container.CopyAsync(bytes, destination)

        // The fixture's owner and runtime connections already exercise TCP admission.
        let! result =
            database.Container.ExecAsync
                [|
                    "psql"
                    "-X"
                    "-At"
                    "-v"
                    "ON_ERROR_STOP=1"
                    "-U"
                    user
                    "-d"
                    "postgres"
                    "-f"
                    destination
                |]

        let output = result.Stdout + "\n" + result.Stderr

        if result.ExitCode <> Nullable 0L then
            failtest ("Schema scenario failed: " + output)

        let expected =
            Regex.Matches(Encoding.UTF8.GetString bytes, "SELECT pg_temp.expect\\('").Count

        let actual = Regex.Matches(output, "PASS [^\\r\\n]+ \\([0-9A-Z]{5}\\)").Count
        Expect.equal actual expected "Every SQLSTATE expectation executed"
        return output
    }

let private structuralScenarios (database: PostgresFixture.Database) =
    task {
        let! _ = populated database
        use! ownerConnection = database.OwnerSource.OpenConnectionAsync(token)

        let! fixturePrivileges =
            DatabaseSession.rows
                ownerConnection
                "SELECT has_database_privilege('claimcore_runtime', current_database(), 'TEMP')"
                [||]
                token

        Expect.isFalse
            (fixturePrivileges |> List.exactlyOne |> DatabaseSession.value<bool> 0)
            "The runtime's deployed privileges exclude temporary objects"

        // The SQLSTATE witness uses a session-local pg_temp function to catch expected failures.
        let! _ =
            DatabaseSession.execute
                ownerConnection
                "GRANT TEMPORARY ON DATABASE postgres TO claimcore_runtime"
                [||]
                token

        let! runtime = runFile database "claimcore_runtime" "SchemaScenarios.sql"
        let! owner = runFile database "claimcore_owner" "OwnerSchemaScenarios.sql"

        for message in
            [
                "PASS unmatched erasure leaves private projections intact"
                "PASS head equals the last ledger row"
            ] do
            Expect.stringContains (runtime + owner) message "State assertion executed"
    }

let private rejected (database: PostgresFixture.Database) statement parameters =
    task {
        use! connection = database.RuntimeSource.OpenConnectionAsync()
        let mutable state = None

        try
            let! _ = DatabaseSession.execute connection statement parameters token in ()
        with :? PostgresException as error ->
            state <- Some error.SqlState

        Expect.equal state (Some "23514") "Permanent scalar format rejects the value"
    }

let private calendarBounds (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        // This fixture suppresses triggers to reach the unchanged CHECK constraints.
        use! connection = database.SuperuserSource.OpenConnectionAsync()

        for value, expected in
            [
                "2026-09-07", None
                "0001-01-01 BC", Some "23514"
                "10000-01-01", Some "23514"
                "infinity", Some "23514"
                "-infinity", Some "23514"
            ] do
            use! transaction = connection.BeginTransactionAsync token

            let! _ =
                DatabaseSession.execute
                    connection
                    "SET LOCAL session_replication_role=replica"
                    [||]
                    token

            let mutable actual = None

            try
                let! count =
                    DatabaseSession.execute
                        connection
                        "UPDATE claimcore.ledger SET business_date=$1::text::date WHERE business_date IS NOT NULL"
                        [| box value |]
                        token

                Expect.equal count 1 "The valid scalar reaches the actual root date column"
            with :? PostgresException as error ->
                actual <- Some error.SqlState

            Expect.equal actual expected "The permanent scalar date range owns refusal"
            do! transaction.RollbackAsync token
    }

let private actorUnicode (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        for scalar in [ 0xFDD0; 0xFDEF; 0xFFFE; 0xFFFF; 0x1FFFE; 0x1FFFF; 0x10FFFE; 0x10FFFF ] do
            let value = "A" + Char.ConvertFromUtf32 scalar + "B"
            do! rejected database "UPDATE claimcore.actors SET subject=$1" [| box value |]
    }

[<Tests>]
let tests =
    testList
        "schema format and append-only boundaries"
        [
            testCaseAsync
                "all runtime and owner SQLSTATE controls and state assertions execute"
                (run structuralScenarios)
            testCaseAsync
                "ledger dates reject BC extended years and infinities"
                (run calendarBounds)
            testCaseAsync "actor SQL checks permanent Unicode" (run actorUnicode)
        ]
