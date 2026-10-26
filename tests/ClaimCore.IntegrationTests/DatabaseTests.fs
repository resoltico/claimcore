module ClaimCore.IntegrationTests.DatabaseTests

open ClaimCore.TestSupport

open ClaimCore.Contract
open ClaimCore.Domain

open System
open System.Security.Cryptography
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreFixtures

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
            (Core.execute
                actor
                (OperationInput.Case(
                    request
                        10
                        "synthetic-a"
                        0L
                        (ClaimCore.Domain.UnvalidatedCaseAct.Open registration)
                ))
                (token))

        let receipt = accepted outcome

        let! second =
            (Core.execute
                actor
                (OperationInput.Case(
                    request
                        11
                        "synthetic-b"
                        0L
                        (ClaimCore.Domain.UnvalidatedCaseAct.Open registration)
                ))
                (token))

        accepted second |> ignore

        return actor, receipt
    }

let private verify (database: PostgresFixture.Database) =
    Verification.verify database.OwnerSource None token

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

let private erased (actor: HandlerContext) =
    task {
        let request =
            actRequest 13 "synthetic-a" 1L (ClaimCore.Domain.UnvalidatedCaseAct.Erase)

        let! result = (Core.execute actor (OperationInput.Case request) (token))

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

let private concurrentInitialization (database: PostgresFixture.Database) =
    task {
        do! sql database "DROP SCHEMA claimcore CASCADE"
        use! blocker = database.OwnerSource.OpenConnectionAsync()
        use! transaction = blocker.BeginTransactionAsync()

        let! _ =
            DatabaseSession.execute
                blocker
                "SELECT pg_advisory_xact_lock(4849322681855856466)"
                [||]
                token

        let attempts =
            [| for _ in 1..8 -> Baseline.initialize database.OwnerSource "Etc/UTC" token |]

        let mutable waiting = false
        let mutable namespacePresent = false

        try
            for _ in 1..100 do
                if not waiting then
                    let! rows =
                        DatabaseSession.rows
                            blocker
                            "SELECT count(*) FROM pg_locks WHERE locktype='advisory' AND NOT granted AND database=(SELECT oid FROM pg_database WHERE datname=current_database())"
                            [||]
                            token

                    waiting <- DatabaseSession.value<int64> 0 rows.Head = 8L

                    if not waiting then
                        do! System.Threading.Tasks.Task.Delay 50

            let! present = Baseline.exists blocker token
            namespacePresent <- present
        finally
            transaction.Rollback()

        let! ids = System.Threading.Tasks.Task.WhenAll attempts
        Expect.isTrue waiting "Every initializer waits before namespace discovery"
        Expect.isFalse namespacePresent "No installation work precedes the administration lock"
        Expect.equal (ids |> Array.distinct |> Array.length) 1 "One immutable installation"
        let! installation = Admission.openRuntime database.RuntimeSource token

        Expect.equal
            installation.InstallationId
            ids[0]
            "Admitted tuple agrees with every initializer"
    }

let private initializationRollback (database: PostgresFixture.Database) =
    task {
        do! sql database "DROP SCHEMA claimcore CASCADE; DROP ROLE claimcore_runtime"

        do!
            refused
                (fun () -> Baseline.initialize database.OwnerSource "Etc/UTC" token)
                "Missing runtime role makes the baseline fail after DDL"

        use! connection = database.OwnerSource.OpenConnectionAsync()
        let! present = Baseline.exists connection token
        Expect.isFalse present "Failed installation leaves no namespace or identity"
        do! sql database "CREATE ROLE claimcore_runtime LOGIN"
        let! _ = Baseline.initialize database.OwnerSource "Etc/UTC" token
        let! _ = Baseline.require connection token
        return ()
    }

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
                "owner provisioning is idempotent and cannot register another initial owner"
                (run (fun database ->
                    task {
                        let! first =
                            LedgerAdministration.provisionOwner
                                database.OwnerSource
                                (id 1)
                                database.Fixture.Owner
                                token

                        match first with
                        | ProvisionOwnerOutcome.Accepted { Subject = ReceiptSubject.Authority _ } ->
                            ()
                        | _ -> failtest "Expected accepted owner provisioning."

                        let! repeated =
                            LedgerAdministration.provisionOwner
                                database.OwnerSource
                                (id 1)
                                database.Fixture.Owner
                                token

                        Expect.isTrue
                            (match repeated with
                             | ProvisionOwnerOutcome.AlreadyAccepted _ -> true
                             | _ -> false)
                            "Exact repeat"

                        let other =
                            PrincipalKey.human "https://issuer.example/" "other"
                            |> Result.defaultWith (string >> failtest)

                        let! otherResult =
                            LedgerAdministration.provisionOwner
                                database.OwnerSource
                                (id 2)
                                other
                                token

                        Expect.equal
                            otherResult
                            (ProvisionOwnerOutcome.Rejected Refusal.ActorAlreadyRegistered)
                            "No second bootstrap"

                        let! changed =
                            LedgerAdministration.provisionOwner
                                database.OwnerSource
                                (id 1)
                                other
                                token

                        Expect.equal
                            changed
                            (ProvisionOwnerOutcome.Rejected Refusal.IdempotencyConflict)
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
