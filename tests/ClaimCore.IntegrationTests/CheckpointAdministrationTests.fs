module ClaimCore.IntegrationTests.CheckpointAdministrationTests

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Postgres
open ClaimCore.Wire
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None
let private sync (pending: System.Threading.Tasks.Task<'a>) = pending.GetAwaiter().GetResult()

let private require result =
    Result.defaultWith (fun error -> failtestf "%A" error) result

let private checkpoints () =
    let database = PostgresFixture.create () |> sync

    try
        let fixture = database.Fixture
        let service = caseWorker fixture |> sync

        use administration =
            new Administration(
                database.Container.GetConnectionString(),
                BuildIdentity.current.Version
            )

        let older = administration.Head token |> sync

        let opened =
            Service.execute
                service
                (Inputs.caseOperation (
                    actRequest fixture 20 "CHECKPOINT" 0L (AuthoredCaseAct.Open Inputs.registration)
                ))
                token
            |> sync
            |> accepted

        let current = administration.Head token |> sync

        Expect.equal
            current.Position
            opened.Position
            "Head couples the current root with installation and frontier"

        Expect.equal
            current.InstallationId
            fixture.InstallationId
            "Installation binding is retained"

        Expect.equal
            (administration.Verify(Some older, token) |> sync)
            (VerificationResult.Verified current.Position)
            "An older valid trusted prefix is accepted in newer history"

        let create installation position frontier =
            InstallationCheckpoint.create installation position frontier |> Option.get

        let foreign = create (Guid.NewGuid()) older.Position None

        let missing =
            create
                fixture.InstallationId
                (LedgerPosition.create (Sequence.successor current.Position.Sequence) EntryHash.zero)
                None

        let changed =
            create
                fixture.InstallationId
                (LedgerPosition.create older.Position.Sequence EntryHash.zero)
                None

        let cutoff = DateTimeOffset.UtcNow.AddMinutes(1).AddTicks(1)

        let cutoff =
            DateTimeOffset(cutoff.UtcTicks - cutoff.UtcTicks % 10L + 1L, TimeSpan.Zero)

        let trusted = create fixture.InstallationId older.Position (Some cutoff)

        for checkpoint, finding in
            [
                foreign, VerificationFinding.CheckpointInstallation
                missing, VerificationFinding.ExpectedPosition
                changed, VerificationFinding.ExpectedPosition
                trusted, VerificationFinding.ExpiryFrontier
            ] do
            match administration.Verify(Some checkpoint, token) |> sync with
            | VerificationResult.Diverged actual ->
                Expect.equal actual.Finding finding "Checkpoint failure has a specific finding"
            | other -> failtestf "Checkpoint must refuse: %A" other

        let sql statement =
            use connection = database.OwnerSource.OpenConnection()
            DatabaseSession.execute connection statement [||] token |> sync |> ignore

        let sessions () =
            use connection = database.OwnerSource.OpenConnection()

            DatabaseSession.rows connection "SELECT count(*) FROM claimcore.sessions" [||] token
            |> sync
            |> List.exactlyOne
            |> DatabaseSession.value<int64> 0

        let addSession () =
            sql
                "WITH observed AS MATERIALIZED(SELECT clock_timestamp() AS instant) INSERT INTO claimcore.sessions SELECT repeat('a',64),decode('01','hex'),1,instant,instant+interval '1 hour',instant,3600 FROM observed"

        let mutable maintenanceRefused = false

        try
            administration.AdmitRestoredInstallation(trusted, token) |> sync |> ignore
        with :? MaintenanceRequiredException ->
            maintenanceRefused <- true

        Expect.isTrue maintenanceRefused "Restore admission requires active maintenance"
        administration.Maintenance(MaintenanceChange.On, token) |> sync |> ignore

        match administration.Rebuild(trusted, token) |> sync with
        | Error actual ->
            Expect.equal
                actual.Finding
                VerificationFinding.ExpiryFrontier
                "Rebuild cannot silently admit a lower restored frontier"
        | Ok _ -> failtest "Rebuild bypassed trusted expiry evidence"

        addSession ()

        let restored =
            administration.AdmitRestoredInstallation(trusted, token) |> sync |> require

        Expect.equal
            restored.InvalidatedSessions
            1L
            "Restore invalidates restored session rows atomically"

        Expect.equal (sessions ()) 0L "Session removal is committed"

        Expect.equal
            restored.Checkpoint.Position
            current.Position
            "Newer accepted history is preserved"

        Expect.equal
            restored.Checkpoint.ExpiredBefore
            (Some cutoff)
            "Restored lower frontier is raised with exact precision"

        Expect.equal
            (administration.Verify(Some trusted, token) |> sync)
            (VerificationResult.Verified current.Position)
            "Raised frontier satisfies trusted evidence"

        administration.Rebuild(trusted, token) |> sync |> require |> ignore

        let lower =
            administration.AdmitRestoredInstallation(older, token) |> sync |> require

        Expect.equal
            lower.Checkpoint.ExpiredBefore
            (Some cutoff)
            "Older/lower evidence cannot lower a current frontier"

        addSession ()

        for checkpoint, finding in
            [
                foreign, VerificationFinding.CheckpointInstallation
                missing, VerificationFinding.ExpectedPosition
                changed, VerificationFinding.ExpectedPosition
            ] do
            match administration.AdmitRestoredInstallation(checkpoint, token) |> sync with
            | Error actual ->
                Expect.equal
                    actual.Finding
                    finding
                    "Restore refuses foreign or missing accepted evidence"
            | Ok _ -> failtest "Invalid accepted prefix was admitted"

            Expect.equal (sessions ()) 1L "Refused admission does not invalidate sessions"

            Expect.equal
                (administration.Head token |> sync).ExpiredBefore
                (Some cutoff)
                "Refusal leaves frontier intact"

        sql
            "CREATE FUNCTION public.reject_restore_delete() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic restore failure' USING ERRCODE='P0001'; END $$; CREATE TRIGGER reject_restore_delete BEFORE DELETE ON claimcore.sessions FOR EACH ROW EXECUTE FUNCTION public.reject_restore_delete()"

        let later = create fixture.InstallationId older.Position (Some(cutoff.AddDays 1))
        let mutable failed = false

        try
            administration.AdmitRestoredInstallation(later, token) |> sync |> ignore
        with :? PostgresException as error ->
            Expect.equal
                error.SqlState
                "P0001"
                "Injected delete failure is reached after cutoff raise"

            failed <- true

        Expect.isTrue failed "Late restore failure was observed"
        Expect.equal (sessions ()) 1L "Failed transaction retains sessions"

        Expect.equal
            (administration.Head token |> sync).ExpiredBefore
            (Some cutoff)
            "Failed transaction rolls back the earlier frontier raise"

        sql
            "DROP TRIGGER reject_restore_delete ON claimcore.sessions; DROP FUNCTION public.reject_restore_delete()"
    finally
        database.Fixture.Dispose()

[<Tests>]
let tests =
    testCase
        "trusted checkpoints preserve older prefixes and admit restored expiry atomically"
        checkpoints
