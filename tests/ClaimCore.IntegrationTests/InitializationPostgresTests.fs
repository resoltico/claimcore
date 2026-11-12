module ClaimCore.IntegrationTests.InitializationPostgresTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Postgres
open ClaimCore.Wire
open ClaimCore.TestSupport
open System.Threading

let private token = CancellationToken.None

let private run action =
    async {
        let! database = PostgresFixture.create () |> Async.AwaitTask

        try
            do! action database |> Async.AwaitTask
        finally
            database.Fixture.Dispose()
    }

let private sql (database: PostgresFixture.Database) statement parameters =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()
        let! _ = DatabaseSession.execute connection statement parameters token
        return ()
    }

/// Only this newly owned fixture is reset, before testing fresh installation.
let private reset database =
    sql database "DROP SCHEMA claimcore CASCADE; DROP EXTENSION pg_trgm" [||]

let private owner (database: PostgresFixture.Database) : InitialOwner =
    {
        Principal = database.Fixture.Owner
        Profile = Inputs.ownerProfile
        Roles = [ Role.CaseHandler; Role.PaymentRecorder; Role.DataSteward; Role.Auditor ]
    }

let private initialization (database: PostgresFixture.Database) =
    task {
        do! reset database

        use administration =
            new Administration(
                database.Container.GetConnectionString(),
                BuildIdentity.current.Version
            )

        let! installation = administration.Initialize("Etc/UTC", owner database, token)
        let! status = administration.Status token
        Expect.equal status.InstallationId installation "One committed installation"

        Expect.equal
            status.Position.Sequence
            Sequence.first
            "Identity, profile, and four additional grants are one complete operation"

        Expect.equal status.Actors 1L "The owner is present with its schema"
        let initial = status.InitialOwner |> Option.get

        Expect.equal
            initial.DisplayName
            (DisplayName.Named(Inputs.name Inputs.ownerProfile.DisplayName))
            "Status identifies the authored owner"

        let settings =
            Npgsql.NpgsqlConnectionStringBuilder(database.Container.GetConnectionString())

        settings.Username <- "claimcore_runtime"
        settings.Password <- "synthetic-only"
        let! runtime = Runtime.openConnection settings.ConnectionString token
        use runtime = runtime |> Result.defaultWith (string >> failtest)

        let service =
            Service.create
                BuildIdentity.current.Version
                installation
                (Runtime.store runtime)
                database.Fixture.Calendar
                database.Fixture.Owner

        let! context = Service.context service () token

        match context with
        | Outcome.Succeeded {
                                Requester = RequesterState.Active context
                            } ->
            Expect.equal
                (context.Roles |> List.map _.Role |> Set.ofList)
                (Set.ofList
                    [
                        Role.Owner
                        Role.CaseHandler
                        Role.PaymentRecorder
                        Role.DataSteward
                        Role.Auditor
                    ])
                "All requested roles are immediately usable"
        | other -> failtestf "Active owner required: %A" other

        let! opened =
            Service.execute
                service
                (Inputs.caseOperation
                    {
                        OperationId = Guid.CreateVersion7()
                        CaseReference = "INITIAL-OWNER"
                        ExpectedRevision = 0L
                        Act = AuthoredCaseAct.Open Inputs.registration
                    })
                token

        Expect.isTrue
            (match opened.Outcome with
             | ExecuteResult.Accepted _ -> true
             | _ -> false)
            "No self-grant step is needed before opening a case"

        let! verified = administration.Verify(None, token)

        Expect.isTrue
            (match verified with
             | VerificationResult.Verified _ -> true
             | _ -> false)
            "Bootstrap extra-role effects replay"

        let mutable refused = false

        try
            let! _ = administration.Initialize("Etc/UTC", owner database, token)
            ()
        with :? InvalidOperationException ->
            refused <- true

        Expect.isTrue refused "Nonempty installation refuses a repeat"

    }

let private rollback (database: PostgresFixture.Database) =
    task {
        do! reset database

        do!
            sql
                database
                "CREATE FUNCTION public.reject_initial_grant() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.role='PAYMENT_RECORDER' THEN RAISE EXCEPTION 'synthetic bootstrap failure'; END IF; RETURN NEW; END $$; CREATE FUNCTION public.arm_initial_failure() RETURNS event_trigger LANGUAGE plpgsql AS $$ DECLARE item record; BEGIN FOR item IN SELECT * FROM pg_event_trigger_ddl_commands() LOOP IF item.object_identity='claimcore.grants' AND item.object_type='table' AND NOT EXISTS(SELECT 1 FROM pg_trigger WHERE tgrelid=to_regclass('claimcore.grants') AND tgname='reject_initial_grant') THEN EXECUTE 'CREATE TRIGGER reject_initial_grant BEFORE INSERT ON claimcore.grants FOR EACH ROW EXECUTE FUNCTION public.reject_initial_grant()'; END IF; END LOOP; END $$; CREATE EVENT TRIGGER arm_initial_failure ON ddl_command_end WHEN TAG IN ('CREATE TABLE') EXECUTE FUNCTION public.arm_initial_failure()"
                [||]

        use administration =
            new Administration(
                database.Container.GetConnectionString(),
                BuildIdentity.current.Version
            )

        let mutable failure = None

        try
            let! _ = administration.Initialize("Etc/UTC", owner database, token)
            ()
        with :? Npgsql.PostgresException as error ->
            failure <- Some(error.SqlState, error.MessageText)

        Expect.equal
            failure
            (Some("P0001", "synthetic bootstrap failure"))
            "The injected late grant failure was reached"

        use! connection = database.OwnerSource.OpenConnectionAsync()
        let! present = Baseline.exists connection token

        Expect.isFalse
            present
            "DDL, owner identity, profile, grant sources and ledger roll back together"
    }

[<Tests>]
let tests =
    testList
        "atomic owner initialization"
        [
            testCaseAsync
                "owner roles are usable at the first runtime connection"
                (run initialization)
            testCaseAsync "late owner grant failure leaves no partial schema" (run rollback)
        ]
