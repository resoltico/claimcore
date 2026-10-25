module ClaimCore.IntegrationTests.SchemaTests

open ClaimCore.TestSupport

open System
open System.IO
open System.Text
open System.Text.RegularExpressions
open Expecto
open Npgsql
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.VerificationSupport

let private runFile (database: PostgresFixture.Database) user name =
    task {
        let path = Path.Combine(AppContext.BaseDirectory, name)
        let bytes = File.ReadAllBytes path
        let destination = "/tmp/" + name
        do! database.Container.CopyAsync(bytes, destination)

        let! result =
            database.Container.ExecAsync
                [|
                    "env"
                    "PGPASSWORD=synthetic-only"
                    "psql"
                    "-X"
                    "-At"
                    "-v"
                    "ON_ERROR_STOP=1"
                    "-h"
                    "127.0.0.1"
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

let private structuralScenarios database =
    task {
        let! runtime = runFile database "claimcore_runtime" "SchemaScenarios.sql"
        let! owner = runFile database "postgres" "OwnerSchemaScenarios.sql"

        for message in
            [
                "PASS erasure clears both case payloads"
                "PASS authority payload untouched by case erasure"
                "PASS erased projection keeps only reference, revision and disposition"
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

        for value in [ "0001-01-01 BC"; "10000-01-01"; "infinity"; "-infinity" ] do
            do!
                rejected
                    database
                    "UPDATE claimcore.cases SET incident_date=$1::date,incident_notification_date=$1::date,payment_decision_date=$1::date,payable_amount=1,payable_currency='EUR',payment_date=$1::date"
                    [| box value |]

        for value in [ "0001-01-01"; "9999-12-31" ] do
            do!
                sql
                    database
                    "UPDATE claimcore.cases SET incident_date=$1::date,incident_notification_date=$1::date,payment_decision_date=$1::date,payable_amount=1,payable_currency='EUR',payment_date=$1::date"
                    [| box value |]
        // The ledger business date has no chronology constraint to mask its own range check.
        let insert =
            "INSERT INTO claimcore.ledger(sequence,previous_hash,entry_hash,entry_format,scope,act,operation_id,actor_id,recorded_at,subject_id,case_revision,business_date,payload_commitment,payload,payload_nonce) SELECT h.sequence+1,h.entry_hash,decode(repeat('ef',32),'hex'),2,'CASE','CLOSE','10000000-0000-4000-8000-000000000099',a.actor_id,clock_timestamp(),c.case_id,2,$1::date,decode(repeat('ab',32),'hex'),decode(repeat('00',9),'hex'),decode(repeat('01',32),'hex') FROM claimcore.ledger_head h CROSS JOIN claimcore.cases c CROSS JOIN claimcore.actors a WHERE a.actor_id='80000000-0000-4000-8000-000000000001'"

        for value in [ "0001-01-01 BC"; "10000-01-01"; "infinity"; "-infinity" ] do
            do! rejected database insert [| box value |]
    }

let private unicodeAndPolicy (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        for value in [ "A\u202eB"; String.replicate 201 "A"; " leading"; "A\u200dB" ] do
            do!
                sql
                    database
                    "UPDATE claimcore.cases SET claimant_name=$1,insurer_name=$1"
                    [| box value |]

        for scalar in [ 0xFDD0; 0xFDEF; 0xFFFE; 0xFFFF; 0x1FFFE; 0x1FFFF; 0x10FFFE; 0x10FFFF ] do
            let value = "A" + Char.ConvertFromUtf32 scalar + "B"
            do! rejected database "UPDATE claimcore.cases SET claimant_name=$1" [| box value |]
            do! rejected database "UPDATE claimcore.cases SET insurer_name=$1" [| box value |]
            do! rejected database "UPDATE claimcore.actors SET subject=$1" [| box value |]


        let! rows =
            task {
                use! connection = database.OwnerSource.OpenConnectionAsync()
                let! _ = DatabaseSession.execute connection "SET DateStyle='SQL, DMY'" [||] token

                return!
                    DatabaseSession.rows
                        connection
                        ($"SELECT {Projections.caseColumns} FROM claimcore.cases")
                        [||]
                        token
            }

        let record = Projections.caseRow rows.Head

        Expect.equal
            record.Snapshot.Reference
            "synthetic-a"
            "Projection dates parse independently of server DateStyle"
    }

let private textByteBounds (database: PostgresFixture.Database) =
    task {
        let! _ = populated database

        for value in [ String('a', 1024); String.replicate 256 "😀"; String(char 1, 1024) ] do
            do!
                sql
                    database
                    "UPDATE claimcore.cases SET claimant_name=$1,insurer_name=$1"
                    [| box value |]

        for value in [ String('a', 1025); String.replicate 257 "😀" ] do
            do! rejected database "UPDATE claimcore.cases SET claimant_name=$1" [| box value |]
            do! rejected database "UPDATE claimcore.cases SET insurer_name=$1" [| box value |]
    }

[<Tests>]
let tests =
    testList
        "schema format and append-only boundaries"
        [
            testCaseAsync
                "names enforce permanent UTF-8 byte bounds independently of admission"
                (run textByteBounds)
            testCaseAsync
                "all runtime and owner SQLSTATE controls and state assertions execute"
                (run structuralScenarios)
            testCaseAsync
                "case and ledger dates reject BC extended years and infinities but accept finite endpoints"
                (run calendarBounds)
            testCaseAsync
                "SQL checks permanent Unicode while retained text ignores current admission policy"
                (run unicodeAndPolicy)
        ]
