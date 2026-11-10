module ClaimCore.IntegrationTests.DatabaseTests

open ClaimCore.TestSupport

open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain
open ClaimCore.Application.Contracts

open System
open System.Security.Cryptography
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.DatabaseScenarios


[<Tests>]
let tests =
    testList
        "PostgreSQL administration"
        [
            testCaseAsync "concurrent initializers install one tuple" (run concurrentInitialization)
            testCaseAsync
                "baseline DDL and identity roll back together"
                (run initializationRollback)
            testCaseAsync
                "initialize refuses existing or invalid installations"
                (run (fun database ->
                    task {
                        for zone in [ "Etc/UTC"; "Europe/Riga"; "invalid/zone" ] do
                            do!
                                refused
                                    (fun () ->
                                        SchemaAdministration.initialize
                                            database.OwnerSource
                                            zone
                                            (fun _ _ ->
                                                System.Threading.Tasks.Task.FromResult(()))
                                            token)
                                    "A nonempty schema is never adopted or changed"
                    }))
            testCaseAsync
                "clean verification reports head and requires the expected position"
                (run (fun database ->
                    task {
                        let! _, receipt = populated database
                        let! head = verify database

                        Expect.equal
                            (position head).Sequence
                            (CaseFixtures.sequence 5L)
                            "Bootstrap, two grants and two case operations each advance once"

                        let! expected = VerificationSupport.checkpoint database

                        Expect.equal
                            expected.Position
                            (position head)
                            "Checkpoint captures the verified current head"

                        let older =
                            InstallationCheckpoint.create
                                expected.InstallationId
                                receipt.Position
                                expected.ExpiredBefore
                            |> Option.get

                        let! prefix = Verification.verify database.OwnerSource (Some older) token

                        Expect.equal
                            prefix
                            (VerificationResult.Verified expected.Position)
                            "A valid older checkpoint prefix remains admissible"

                        let! _ = Verification.verify database.OwnerSource (Some expected) token

                        let! wrong =
                            Verification.verify
                                database.OwnerSource
                                (Some(
                                    InstallationCheckpoint.create
                                        expected.InstallationId
                                        { expected.Position with
                                            EntryHash = EntryHash.zero
                                        }
                                        expected.ExpiredBefore
                                    |> Option.get
                                ))
                                token

                        diverged VerificationFinding.ExpectedPosition wrong

                        let! future =
                            Verification.verify
                                database.OwnerSource
                                (Some(
                                    InstallationCheckpoint.create
                                        expected.InstallationId
                                        { expected.Position with
                                            Sequence = CaseFixtures.sequence 999L
                                        }
                                        expected.ExpiredBefore
                                    |> Option.get
                                ))
                                token

                        diverged VerificationFinding.ExpectedPosition future
                    }))
            for name, change in
                [
                    "absent schema", "DROP SCHEMA claimcore CASCADE"
                    "foreign schema",
                    "DROP SCHEMA claimcore CASCADE; CREATE SCHEMA claimcore; GRANT USAGE ON SCHEMA claimcore TO claimcore_runtime"
                    "missing baseline identity", "DELETE FROM claimcore.baseline_identity"
                    "unknown baseline identity",
                    "UPDATE claimcore.baseline_identity SET identifier='unknown'"
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
                                    (fun () ->
                                        InstallationCheck.openRuntime database.RuntimeSource token)
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
                        (fun () -> InstallationCheck.transport settings)
                        "Unverified remote TLS rejected"

                let verified =
                    Npgsql.NpgsqlConnectionStringBuilder("Host=remote.example;SSL Mode=VerifyFull")

                InstallationCheck.transport verified)
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

                        let! receipt = erased database.Fixture actor

                        let! expected = VerificationSupport.checkpoint database

                        Expect.equal
                            expected.Position
                            receipt.Position
                            "Checkpoint preserves the accepted root proof"

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
            testCase "baseline is embedded with its exact source digest" (fun () ->
                let root = ClaimCore.TestSupport.RepositoryRoot.find ()
                let bytes = IO.File.ReadAllBytes(IO.Path.Combine(root, "db/baseline.sql"))
                Expect.equal Baseline.digest (SHA256.HashData bytes) "Exact source bytes")
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
                            (fun () -> Baseline.zone zone |> ignore)
                            "Canonical IANA zone required")
            testCaseAsync
                "public administration opens one source and refuses foreign schemas untouched"
                (run (fun database ->
                    task {
                        use administration =
                            new Administration(
                                database.Container.GetConnectionString(),
                                BuildIdentity.current.Version
                            )

                        do!
                            refused
                                (fun () ->
                                    administration.Initialize(
                                        "Etc/UTC",
                                        initialOwner database,
                                        token
                                    ))
                                "Public initialization refuses an existing schema"

                        let! empty = administration.Verify(None, token)

                        Expect.equal
                            (position empty).Sequence
                            (CaseFixtures.sequence (0L))
                            "Public verifier reports genesis"

                        do!
                            sql
                                database
                                "DROP SCHEMA claimcore CASCADE; CREATE SCHEMA claimcore; CREATE TABLE claimcore.foreign_marker(value int); INSERT INTO claimcore.foreign_marker VALUES (17)"

                        do!
                            refused
                                (fun () ->
                                    administration.Initialize(
                                        "Etc/UTC",
                                        initialOwner database,
                                        token
                                    ))
                                "Initializer refuses foreign schema"

                        do!
                            refused
                                (fun () -> administration.Verify(None, token))
                                "Verify refuses foreign schema"

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
                        let! receipt = erased database.Fixture actor

                        let! expected = VerificationSupport.checkpoint database

                        Expect.equal
                            expected.Position
                            receipt.Position
                            "Checkpoint preserves the accepted root proof"

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
                                    (fun () ->
                                        InstallationCheck.openRuntime database.RuntimeSource token)
                                    "Owner authority is refused"
                        }))
        ]
