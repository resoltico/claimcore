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
open Npgsql
open ClaimCore.Application
open ClaimCore.Application.Administration
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.DatabaseScenarios


let private expectProfileRefusal name (action: unit -> System.Threading.Tasks.Task<'a>) =
    task {
        let! fault =
            task {
                try
                    let! _ = action ()
                    return None
                with :? OperatorFaultException as error ->
                    return Some error.Fault
            }

        Expect.equal fault (Some OperatorFault.UnsupportedDatabase) name
    }

let private databaseProfile (database: PostgresFixture.Database) =
    task {
        use! runtimeConnection = database.RuntimeSource.OpenConnectionAsync(token)
        do! DatabaseProfile.require runtimeConnection token
        let! checkpoint = VerificationSupport.checkpoint database

        // The cluster operator provisions the deliberate locale mismatch.
        use! bootstrap = database.SuperuserSource.OpenConnectionAsync()

        let! _ =
            DatabaseSession.execute
                bootstrap
                "CREATE DATABASE claimcore_libc OWNER claimcore_owner TEMPLATE template0 LOCALE_PROVIDER libc LOCALE 'C'"
                [||]
                token

        let ownerSettings =
            Npgsql.NpgsqlConnectionStringBuilder(PostgresFixture.ownerConnectionString database)

        ownerSettings.Database <- "claimcore_libc"

        use administration =
            new Administration(
                ownerSettings.ConnectionString,
                BuildIdentity.current.Version,
                database.MasterKey
            )

        let handle =
            MaintenanceHandle.create 1L (Guid.Parse "10000000-0000-4000-8000-000000000001")
            |> Option.get

        let owner = initialOwner database

        do!
            expectProfileRefusal "Initialize" (fun () ->
                administration.Initialize("Etc/UTC", owner, None, token))

        do!
            expectProfileRefusal "Initialize if missing" (fun () ->
                administration.InitializeIfMissing("Etc/UTC", owner, None, token))

        do! expectProfileRefusal "Status" (fun () -> administration.Status token)
        do! expectProfileRefusal "Checkpoint capture" (fun () -> administration.Head token)
        do! expectProfileRefusal "Verification" (fun () -> administration.Verify(None, token))

        do!
            expectProfileRefusal "Rebuild" (fun () ->
                administration.Rebuild(checkpoint, handle, token))

        do!
            expectProfileRefusal "Restore admission" (fun () ->
                administration.AdmitRestoredInstallation(checkpoint, handle, token))

        do!
            expectProfileRefusal "Owner recovery" (fun () ->
                administration.RecoverOwner(
                    {
                        ActorId = None
                        Principal = owner.Principal
                        Name = None
                    },
                    token
                ))

        let runtimeSettings =
            Npgsql.NpgsqlConnectionStringBuilder(PostgresFixture.runtimeConnectionString database)

        runtimeSettings.Database <- ownerSettings.Database

        let! startup =
            Runtime.openConnection runtimeSettings.ConnectionString database.MasterKey token

        match startup with
        | Error RuntimeOpenFault.UnsupportedDatabase -> ()
        | Error fault -> failtestf "Expected unsupported database at startup, got %A" fault
        | Ok runtime ->
            runtime.Dispose()
            failtest "The Web runtime admitted a libc database."

        use source = Npgsql.NpgsqlDataSource.Create(ownerSettings.ConnectionString)
        use! connection = source.OpenConnectionAsync(token)
        let! present = Baseline.exists connection token
        Expect.isFalse present "Refusals leave the second database uninitialized"
    }

let private ownerAdmission (database: PostgresFixture.Database) =
    task {
        use owner =
            new Administration(
                PostgresFixture.ownerConnectionString database,
                BuildIdentity.current.Version,
                database.MasterKey
            )

        let! status = owner.Status token

        Expect.equal
            status.InstallationId
            database.Installation.InstallationId
            "The ordinary database owner is admitted"

        let refusesOwner name (candidate: Administration) =
            task {
                let! observed =
                    task {
                        try
                            let! _ = candidate.Status token
                            return None
                        with :? OperatorFaultException as error ->
                            return Some error.Fault
                    }

                Expect.equal observed (Some OperatorFault.PermissionDenied) name
            }

        // Each unsafe attribute is injected independently by the synthetic cluster operator.
        use! bootstrap = database.SuperuserSource.OpenConnectionAsync()

        for attribute in [ "SUPERUSER"; "CREATEROLE"; "REPLICATION"; "BYPASSRLS" ] do
            let! _ =
                DatabaseSession.execute
                    bootstrap
                    $"ALTER ROLE claimcore_owner {attribute}"
                    [||]
                    token

            do! refusesOwner ($"The database owner with {attribute} is refused") owner

            let! _ =
                DatabaseSession.execute
                    bootstrap
                    $"ALTER ROLE claimcore_owner NO{attribute}"
                    [||]
                    token

            ()

        let impersonatingSettings =
            Npgsql.NpgsqlConnectionStringBuilder(database.Container.GetConnectionString())

        impersonatingSettings.Options <- "-c role=claimcore_owner"

        use impersonatingSource =
            Npgsql.NpgsqlDataSource.Create(impersonatingSettings.ConnectionString)

        use! impersonatingConnection = impersonatingSource.OpenConnectionAsync(token)

        let! identity =
            DatabaseSession.rows
                impersonatingConnection
                "SELECT session_user::text, current_user::text"
                [||]
                token

        let role = List.exactlyOne identity

        Expect.equal
            (DatabaseSession.value<string> 0 role)
            "postgres"
            "The authenticated role is still the superuser"

        Expect.equal
            (DatabaseSession.value<string> 1 role)
            "claimcore_owner"
            "The startup option selected the owner role"

        use impersonating =
            new Administration(
                impersonatingSettings.ConnectionString,
                BuildIdentity.current.Version,
                database.MasterKey
            )

        do!
            refusesOwner
                "An authenticated superuser with its current role set to the owner is refused"
                impersonating

        use runtime =
            new Administration(
                PostgresFixture.runtimeConnectionString database,
                BuildIdentity.current.Version,
                database.MasterKey
            )

        do! refusesOwner "A role that does not own the database is refused" runtime

        let! restored = owner.Status token

        Expect.equal
            restored.InstallationId
            status.InstallationId
            "Restoring the admitted attributes permits status"
    }

[<Tests>]
let tests =
    testList
        "PostgreSQL administration"
        [
            testCaseAsync
                "storage reads use a repeatable snapshot and PostgreSQL rejects writes"
                (run (fun database ->
                    task {
                        let! sqlState =
                            task {
                                try
                                    return!
                                        DatabaseSession.read
                                            database.OwnerSource
                                            token
                                            (fun connection ->
                                                task {
                                                    let! settings =
                                                        DatabaseSession.rows
                                                            connection
                                                            "SHOW transaction_isolation"
                                                            [||]
                                                            token

                                                    Expect.equal
                                                        (DatabaseSession.value<string>
                                                            0
                                                            settings.Head)
                                                        "repeatable read"
                                                        "Reads retain their snapshot isolation"

                                                    let! _ =
                                                        DatabaseSession.execute
                                                            connection
                                                            "UPDATE claimcore.installation SET installation_format = installation_format WHERE false"
                                                            [||]
                                                            token

                                                    return None
                                                })
                                with :? PostgresException as error ->
                                    return Some error.SqlState
                            }

                        Expect.equal
                            sqlState
                            (Some PostgresErrorCodes.ReadOnlySqlTransaction)
                            "PostgreSQL enforces the read boundary"
                    }))
            testCaseAsync
                "installation owner admission rejects unsafe role attributes and a non-owner"
                (run ownerAdmission)
            testCaseAsync
                "owner and Web refuse a libc database before accessing installation state"
                (run databaseProfile)
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
                                            database.MasterKey
                                            zone
                                            (fun _ _ _ ->
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

                        let! prefix =
                            Verification.verify
                                database.OwnerSource
                                database.MasterKey
                                (Some older)
                                token

                        Expect.equal
                            prefix
                            (VerificationResult.Verified expected.Position)
                            "A valid older checkpoint prefix remains admissible"

                        let! _ =
                            Verification.verify
                                database.OwnerSource
                                database.MasterKey
                                (Some expected)
                                token

                        let! wrong =
                            Verification.verify
                                database.OwnerSource
                                database.MasterKey
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
                                database.MasterKey
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
                            if name = "schema owner" || name = "superuser" then
                                // Exercise unsafe roles through explicit cluster-operator setup.
                                use! bootstrap = database.SuperuserSource.OpenConnectionAsync()
                                let! _ = DatabaseSession.execute bootstrap change [||] token
                                ()
                            else
                                do! sql database change

                            do!
                                refused
                                    (fun () ->
                                        InstallationCheck.openRuntime
                                            database.RuntimeSource
                                            database.MasterKey
                                            token)
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

                        let! _ =
                            Verification.verify
                                database.OwnerSource
                                database.MasterKey
                                (Some expected)
                                token

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
                            Verification.verify
                                database.OwnerSource
                                database.MasterKey
                                (Some expected)
                                token

                        diverged VerificationFinding.ExpectedPosition restored
                    }))
            testCase "baseline is embedded with its exact source digest" (fun () ->
                let root = ClaimCore.TestSupport.RepositoryRoot.find ()
                let bytes = IO.File.ReadAllBytes(IO.Path.Combine(root, "db/baseline.sql"))
                Expect.equal Baseline.digest (SHA256.HashData bytes) "Exact source bytes")
            testCase "only canonical IANA zones pass initialization" (fun () ->
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
                                PostgresFixture.ownerConnectionString database,
                                BuildIdentity.current.Version,
                                database.MasterKey
                            )

                        do!
                            refused
                                (fun () ->
                                    administration.Initialize(
                                        "Etc/UTC",
                                        initialOwner database,
                                        None,
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

                        let! skipFault =
                            task {
                                try
                                    let! _ =
                                        administration.InitializeIfMissing(
                                            "Etc/UTC",
                                            initialOwner database,
                                            None,
                                            token
                                        )

                                    return None
                                with :? OperatorFaultException as error ->
                                    return Some error.Fault
                            }

                        Expect.equal
                            skipFault
                            (Some OperatorFault.SchemaMismatch)
                            "Skip-existing refuses a foreign schema"

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

                        let! _ =
                            Verification.verify
                                database.OwnerSource
                                database.MasterKey
                                (Some expected)
                                token

                        do!
                            sql
                                database
                                "UPDATE claimcore.cases SET case_reference='forged-reference' WHERE case_reference='synthetic-a'"

                        let! changed =
                            Verification.verify
                                database.OwnerSource
                                database.MasterKey
                                (Some expected)
                                token

                        diverged VerificationFinding.CaseProjection changed
                    }))
            for inheritance in [ true; false ] do
                testCaseAsync
                    ($"runtime cannot inherit or assume schema ownership: inherit={inheritance}")
                    (run (fun database ->
                        task {
                            // Simulate a cluster operator granting the runtime schema ownership.
                            use! bootstrap = database.SuperuserSource.OpenConnectionAsync()

                            let! _ =
                                DatabaseSession.execute
                                    bootstrap
                                    "CREATE ROLE schema_owner; ALTER SCHEMA claimcore OWNER TO schema_owner"
                                    [||]
                                    token

                            let grant =
                                if inheritance then
                                    "GRANT schema_owner TO claimcore_runtime WITH INHERIT TRUE, SET TRUE"
                                else
                                    "GRANT schema_owner TO claimcore_runtime WITH INHERIT FALSE, SET TRUE"

                            let! _ = DatabaseSession.execute bootstrap grant [||] token
                            use! connection = database.RuntimeSource.OpenConnectionAsync()

                            let! rows =
                                DatabaseSession.rows
                                    connection
                                    "SELECT n.nspowner=r.oid,pg_has_role(r.oid,n.nspowner,'USAGE'),pg_has_role(r.oid,n.nspowner,'SET') FROM pg_roles r CROSS JOIN pg_namespace n WHERE r.rolname=current_user AND n.nspname='claimcore'"
                                    [||]
                                    token

                            Expect.isFalse
                                (DatabaseSession.value<bool> 0 rows.Head)
                                "Direct ownership alone does not reveal this people"

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
                                        InstallationCheck.openRuntime
                                            database.RuntimeSource
                                            database.MasterKey
                                            token)
                                    "Owner people is refused"
                        }))
        ]
