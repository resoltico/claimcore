module ClaimCore.IntegrationTests.DatabaseTests

open ClaimCore.Catalog

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreContractTests

let private token = CancellationToken.None

let private run action =
    async {
        let! database = PostgresFixture.create () |> Async.AwaitTask

        try
            do! action database |> Async.AwaitTask
        finally
            database.Fixture.Dispose()
    }

let private sql (database: PostgresFixture.Database) text =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()
        let! _ = DatabaseSession.execute connection text [||] token
        return ()
    }

let private refused action message =
    task {
        let mutable refused = false

        try
            let! _ = action () in ()
        with _ ->
            refused <- true

        Expect.isTrue refused message
    }

let private populated (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture

        let! outcome =
            actor.Execute(
                request 10 "synthetic-a" 0L (ClaimCore.Domain.BusinessCommand.Open registration),
                token
            )

        let receipt = accepted outcome

        let! second =
            actor.Execute(
                request 11 "synthetic-b" 0L (ClaimCore.Domain.BusinessCommand.Open registration),
                token
            )

        accepted second |> ignore

        return actor, receipt
    }

let private verify (database: PostgresFixture.Database) =
    Verification.verify database.OwnerSource None token

let private rollbackChecked (database: PostgresFixture.Database) =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows
                connection
                "SELECT max(version),to_regclass('claimcore.rollback_fixture')::text FROM claimcore.schema_migrations"
                [||]
                token

        Expect.equal (DatabaseSession.value<int> 0 rows.Head) 2 "No failed version recorded"

        Expect.isNone (DatabaseSession.optional<string> 1 rows.Head) "DDL rolled back"
    }

let private erasureFixture (database: PostgresFixture.Database) =
    task {
        let! actor, _ = populated database

        let! granted =
            grant
                actor
                (id 12)
                database.Fixture.Owner
                Role.DataSteward
                GrantTarget.Installation
                true

        acceptedAuthority granted
        return actor
    }

let private erased (actor: IActorClaimsCore) =
    task {
        let request =
            actRequest
                13
                "synthetic-a"
                1L
                (ClaimCore.Domain.CaseAct.Stewardship ClaimCore.Domain.StewardshipAct.Erase)

        let! result = actor.Execute(request, token)
        return accepted result
    }

let private position =
    function
    | VerificationResult.Verified position -> position
    | VerificationResult.Diverged _ -> failtest "Expected consistent ledger"

let private diverged code =
    function
    | VerificationResult.Diverged finding ->
        Expect.equal finding.Finding code "Exact verification divergence"
    | _ -> failtest "Expected typed divergence"

let private exec (database: PostgresFixture.Database) arguments =
    task {
        let! result = database.Container.ExecAsync arguments

        Expect.equal
            result.ExitCode
            (Nullable 0L)
            "Synthetic database backup/restore command succeeds"
    }

[<Tests>]
let tests =
    testList
        "PostgreSQL administration"
        [
            testCaseAsync
                "initialize repeats without changing identity and rejects a different zone"
                (run (fun database ->
                    task {
                        let! id =
                            SchemaAdministration.initialize database.OwnerSource "Etc/UTC" token

                        Expect.equal id database.Fixture.InstallationId "Same installation"

                        do!
                            refused
                                (fun () ->
                                    SchemaAdministration.initialize
                                        database.OwnerSource
                                        "Europe/Riga"
                                        token)
                                "Different zone rejected"

                        do!
                            refused
                                (fun () ->
                                    SchemaAdministration.initialize
                                        database.OwnerSource
                                        "invalid/zone"
                                        token)
                                "Invalid zone rejected"
                    }))
            testCaseAsync
                "migration two is applied once, checksummed and rolls back on failure"
                (run (fun database ->
                    task {
                        let fixture sql =
                            {
                                Version = 2
                                Name = "fixture"
                                Sql = sql
                                Digest = SHA256.HashData(Encoding.UTF8.GetBytes sql)
                            }

                        let script = fixture "CREATE TABLE claimcore.fixture(value int)"

                        do!
                            Migrations.migrateWith
                                database.OwnerSource
                                (Migrations.scripts @ [ script ])
                                token

                        do!
                            Migrations.migrateWith
                                database.OwnerSource
                                (Migrations.scripts @ [ script ])
                                token

                        do!
                            refused
                                (fun () ->
                                    Migrations.migrateWith
                                        database.OwnerSource
                                        (Migrations.scripts @ [ fixture "SELECT 1" ])
                                        token)
                                "Edited digest rejected"

                        let third =
                            { script with
                                Version = 3
                                Name = "failure"
                                Sql =
                                    "CREATE TABLE claimcore.rollback_fixture(value int); SELECT 1/0"
                                Digest =
                                    SHA256.HashData(
                                        Encoding.UTF8.GetBytes
                                            "CREATE TABLE claimcore.rollback_fixture(value int); SELECT 1/0"
                                    )
                            }

                        do!
                            refused
                                (fun () ->
                                    Migrations.migrateWith
                                        database.OwnerSource
                                        (Migrations.scripts @ [ script; third ])
                                        token)
                                "Migration failure surfaced"

                        do! rollbackChecked database
                    }))
            testCaseAsync
                "owner provisioning is idempotent and cannot register another initial owner"
                (run (fun database ->
                    task {
                        let! first =
                            LedgerAdministration.provisionOwner
                                database.OwnerSource
                                (id 1)
                                database.Fixture.Owner
                                token

                        acceptedAuthority first

                        let! repeated =
                            LedgerAdministration.provisionOwner
                                database.OwnerSource
                                (id 1)
                                database.Fixture.Owner
                                token

                        Expect.isTrue
                            (match repeated with
                             | ExecuteOutcome.AlreadyAccepted _ -> true
                             | _ -> false)
                            "Exact repeat"

                        let other =
                            PrincipalKey.human "https://issuer.example/" "other"
                            |> Result.defaultWith failtest

                        let! otherResult =
                            LedgerAdministration.provisionOwner
                                database.OwnerSource
                                (id 2)
                                other
                                token

                        Expect.equal
                            otherResult
                            (ExecuteOutcome.Rejected Rejection.ActorAlreadyRegistered)
                            "No second bootstrap"

                        let! changed =
                            LedgerAdministration.provisionOwner
                                database.OwnerSource
                                (id 1)
                                other
                                token

                        Expect.equal
                            changed
                            (ExecuteOutcome.Rejected Rejection.IdempotencyConflict)
                            "Different principal is a conflict"
                    }))
            testCaseAsync
                "clean verification reports head and requires the expected position"
                (run (fun database ->
                    task {
                        let! _, receipt = populated database
                        let! head = verify database
                        Expect.equal (position head).Sequence 5L "Five accepted entries"

                        let expected =
                            {
                                ExpectedPosition.Sequence = receipt.Position.Sequence
                                EntryHash = Convert.FromHexString receipt.Position.EntryHash
                            }

                        let! _ = Verification.verify database.OwnerSource (Some expected) token

                        let! wrong =
                            Verification.verify
                                database.OwnerSource
                                (Some
                                    { expected with
                                        EntryHash = Array.zeroCreate 32
                                    })
                                token

                        diverged VerificationFinding.ExpectedPosition wrong

                        let! future =
                            Verification.verify
                                database.OwnerSource
                                (Some { expected with Sequence = 999L })
                                token

                        diverged VerificationFinding.ExpectedPosition future
                    }))
            for name, change in
                [
                    "absent schema", "DROP SCHEMA claimcore CASCADE"
                    "foreign schema",
                    "DROP SCHEMA claimcore CASCADE; CREATE SCHEMA claimcore; GRANT USAGE ON SCHEMA claimcore TO claimcore_runtime"
                    "behind migration", "DELETE FROM claimcore.schema_migrations"
                    "ahead migration",
                    "INSERT INTO claimcore.schema_migrations VALUES (2,'extra',decode(repeat('00',32),'hex'),clock_timestamp())"
                    "missing installation", "DELETE FROM claimcore.installation"
                    "unresolvable zone",
                    "UPDATE claimcore.installation SET business_time_zone='Invalid/Zone'"
                    "wrong head", "UPDATE claimcore.ledger_head SET sequence=1"
                    "schema owner", "ALTER SCHEMA claimcore OWNER TO claimcore_runtime"
                    "superuser", "ALTER ROLE claimcore_runtime SUPERUSER"
                ] do
                testCaseAsync
                    ($"runtime admission refuses {name}")
                    (run (fun database ->
                        task {
                            do! sql database change

                            do!
                                refused
                                    (fun () -> Admission.openRuntime database.RuntimeSource token)
                                    $"{name} rejected"
                        }))
            testCase "runtime admission refuses remote connections without verified TLS" (fun () ->
                for mode in
                    [
                        Npgsql.SslMode.Disable
                        Npgsql.SslMode.Require
                        Npgsql.SslMode.VerifyCA
                        Npgsql.SslMode.Prefer
                    ] do
                    let settings = Npgsql.NpgsqlConnectionStringBuilder()
                    settings.Host <- "remote.example"
                    settings.SslMode <- mode

                    Expect.throws
                        (fun () -> Admission.transport settings)
                        "Unverified remote TLS rejected"

                let verified =
                    Npgsql.NpgsqlConnectionStringBuilder("Host=remote.example;SSL Mode=VerifyFull")

                Admission.transport verified)
            testCaseAsync
                "post-erasure expectation rejects an actual pre-erasure backup restore"
                (run (fun database ->
                    task {
                        let! actor = erasureFixture database

                        do!
                            exec
                                database
                                [|
                                    "pg_dump"
                                    "-U"
                                    "postgres"
                                    "-d"
                                    "postgres"
                                    "-n"
                                    "claimcore"
                                    "-f"
                                    "/tmp/before-erasure.sql"
                                |]

                        let! receipt = erased actor

                        let expected =
                            {
                                ExpectedPosition.Sequence = receipt.Position.Sequence
                                EntryHash = Convert.FromHexString receipt.Position.EntryHash
                            }

                        let! _ = Verification.verify database.OwnerSource (Some expected) token
                        do! sql database "DROP SCHEMA claimcore CASCADE"

                        do!
                            exec
                                database
                                [|
                                    "psql"
                                    "-U"
                                    "postgres"
                                    "-d"
                                    "postgres"
                                    "-v"
                                    "ON_ERROR_STOP=1"
                                    "-f"
                                    "/tmp/before-erasure.sql"
                                |]

                        let! _ = verify database

                        let! restored =
                            Verification.verify database.OwnerSource (Some expected) token

                        diverged VerificationFinding.ExpectedPosition restored
                    }))
            testCase "all migration files are embedded with their exact byte digests" (fun () ->
                let root = ClaimCore.TestSupport.RepositoryRoot.find ()

                let files =
                    IO.Directory.GetFiles(IO.Path.Combine(root, "db/migrations"), "*.sql")
                    |> Array.sort

                Expect.equal
                    files.Length
                    Migrations.scripts.Length
                    "Every script reaches the executable"

                for index in 0 .. files.Length - 1 do
                    Expect.equal
                        Migrations.scripts[index].Version
                        (index + 1)
                        "Versions are contiguous"

                    Expect.equal
                        Migrations.scripts[index].Digest
                        (SHA256.HashData(IO.File.ReadAllBytes files[index]))
                        "Raw script bytes are checksum-bound")
            testCase
                "supported server majors enforce their minimum minor and reject unlisted majors"
                (fun () ->
                    ServerSupport.require (System.Version(18, 6))
                    ServerSupport.require (System.Version(18, 7))

                    for version in
                        [ System.Version(18, 5); System.Version(17, 9); System.Version(19, 0) ] do
                        Expect.throws
                            (fun () -> ServerSupport.require version)
                            "Unqualified major or too-old minor refuses"

                    for zone in [ "US/Eastern"; "Europe/Riga "; "UTC"; "Eastern Standard Time" ] do
                        Expect.throws
                            (fun () -> Migrations.zone zone |> ignore)
                            "Canonical IANA zone required")
            testCaseAsync
                "public administration opens one source and refuses foreign schemas untouched"
                (run (fun database ->
                    task {
                        use administration =
                            new Administration(database.Container.GetConnectionString())

                        let! same = administration.Initialize("Etc/UTC", token)

                        Expect.equal
                            same
                            database.Fixture.InstallationId
                            "Public initializer is idempotent"

                        let! empty = administration.Verify(None, token)
                        Expect.equal (position empty).Sequence 0L "Public verifier reports genesis"

                        do!
                            sql
                                database
                                "DROP SCHEMA claimcore CASCADE; CREATE SCHEMA claimcore; CREATE TABLE claimcore.foreign_marker(value int); INSERT INTO claimcore.foreign_marker VALUES (17)"

                        do!
                            refused
                                (fun () -> administration.Initialize("Etc/UTC", token))
                                "Initializer refuses foreign schema"

                        do!
                            refused
                                (fun () -> administration.Migrate(token))
                                "Migrate refuses foreign schema"

                        do!
                            refused
                                (fun () -> administration.Verify(None, token))
                                "Verify refuses foreign schema"

                        do!
                            refused
                                (fun () ->
                                    administration.ProvisionOwner(
                                        id 1,
                                        database.Fixture.Owner,
                                        token
                                    ))
                                "Provision refuses foreign schema"

                        use! connection = database.OwnerSource.OpenConnectionAsync()

                        let! rows =
                            DatabaseSession.rows
                                connection
                                "SELECT value FROM claimcore.foreign_marker"
                                [||]
                                token

                        Expect.equal
                            (DatabaseSession.value<int> 0 rows.Head)
                            17
                            "Foreign state was not changed"
                    }))
            testCaseAsync
                "retained erased reference key remains bound to ledger subject identity"
                (run (fun database ->
                    task {
                        let! actor = erasureFixture database
                        let! receipt = erased actor

                        let expected =
                            {
                                ExpectedPosition.Sequence = receipt.Position.Sequence
                                EntryHash = Convert.FromHexString receipt.Position.EntryHash
                            }

                        let! _ = Verification.verify database.OwnerSource (Some expected) token

                        do!
                            sql
                                database
                                "UPDATE claimcore.cases SET case_reference='forged-reference' WHERE case_reference='synthetic-a'"

                        let! changed =
                            Verification.verify database.OwnerSource (Some expected) token

                        diverged VerificationFinding.CaseProjection changed
                    }))
            for inheritance in [ true; false ] do
                testCaseAsync
                    ($"runtime cannot inherit or assume schema ownership: inherit={inheritance}")
                    (run (fun database ->
                        task {
                            do!
                                sql
                                    database
                                    "CREATE ROLE schema_owner; ALTER SCHEMA claimcore OWNER TO schema_owner"

                            let grant =
                                if inheritance then
                                    "GRANT schema_owner TO claimcore_runtime"
                                else
                                    "GRANT schema_owner TO claimcore_runtime WITH INHERIT FALSE, SET TRUE"

                            do! sql database grant
                            use! connection = database.RuntimeSource.OpenConnectionAsync()

                            let! rows =
                                DatabaseSession.rows
                                    connection
                                    "SELECT n.nspowner=r.oid,pg_has_role(r.oid,n.nspowner,'USAGE'),pg_has_role(r.oid,n.nspowner,'SET') FROM pg_roles r CROSS JOIN pg_namespace n WHERE r.rolname=current_user AND n.nspname='claimcore'"
                                    [||]
                                    token

                            Expect.isFalse
                                (DatabaseSession.value<bool> 0 rows.Head)
                                "Direct ownership alone does not reveal this authority"

                            Expect.equal
                                (DatabaseSession.value<bool> 1 rows.Head)
                                inheritance
                                "Effective inherited ownership is measured"

                            Expect.isTrue
                                (DatabaseSession.value<bool> 2 rows.Head)
                                "Session can assume the owner role"

                            do!
                                refused
                                    (fun () -> Admission.openRuntime database.RuntimeSource token)
                                    "Owner authority is refused"
                        }))
        ]
