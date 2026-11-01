module ClaimCore.IntegrationTests.ProjectionRebuildFailureTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private image (database: PostgresFixture.Database) =
    task {
        let result = ResizeArray<string * obj array list>()

        for table in
            [
                "baseline_identity"
                "installation"
                "register_maintenance"
                "ledger"
                "ledger_head"
                "cases"
                "actors"
                "grants"
                "operation_fences"
                "sessions"
            ] do
            use! connection = database.OwnerSource.OpenConnectionAsync()

            let! values =
                DatabaseSession.rows
                    connection
                    ("SELECT row_to_json(t)::text FROM claimcore." + table + " t ORDER BY 1")
                    [||]
                    token

            result.Add(table, values)

        return List.ofSeq result
    }

let private seed database =
    task {
        let! actor = populated database
        let! _ = resolve actor { OperationId = id 99 } token
        let! checkpoint = verified database
        do! enterMaintenance database
        return actor, checkpoint
    }

let private trigger database body =
    sql
        database
        ("CREATE FUNCTION public.rebuild_case_control() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN "
         + body
         + " RETURN NEW; END $$; CREATE TRIGGER rebuild_case_control BEFORE INSERT ON claimcore.cases FOR EACH ROW EXECUTE FUNCTION public.rebuild_case_control()")
        [||]

let private waitFor (database: PostgresFixture.Database) statement =
    task {
        let timer = Diagnostics.Stopwatch.StartNew()
        let mutable found = false

        while not found && timer.Elapsed < TimeSpan.FromSeconds 3. do
            use! connection = database.OwnerSource.OpenConnectionAsync()
            let! values = DatabaseSession.rows connection statement [||] token
            found <- DatabaseSession.value<bool> 0 values.Head

            if not found then
                do! Task.Delay 20

        Expect.isTrue found "The real PostgreSQL synchronization boundary must be reached"
    }

let private advisoryWait =
    "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event='advisory')"

let private corruptHistory mutation database =
    task {
        let! _, checkpoint = seed database

        do!
            sql
                database
                ("ALTER TABLE claimcore.ledger DISABLE TRIGGER USER; "
                 + mutation
                 + "; ALTER TABLE claimcore.ledger ENABLE TRIGGER USER")
                [||]

        let! before = image database
        let! result = ProjectionRebuild.rebuild database.OwnerSource checkpoint token
        Expect.isError result "Corrupt authoritative history cannot authorize reconstruction"
        let! after = image database
        Expect.equal after before "Refusal changes no retained or derived bytes"
    }

let private corruptHead database =
    task {
        let! _, checkpoint = seed database

        do!
            sql
                database
                "ALTER TABLE claimcore.ledger_head DISABLE TRIGGER USER; UPDATE claimcore.ledger_head SET entry_hash=decode(repeat('00',32),'hex'); ALTER TABLE claimcore.ledger_head ENABLE TRIGGER USER"
                [||]

        let! before = image database
        let! result = ProjectionRebuild.rebuild database.OwnerSource checkpoint token

        match result with
        | Error finding ->
            Expect.equal
                finding.Finding
                VerificationFinding.Head
                "Authoritative head disagreement refuses replacement"
        | Ok _ -> failtest "A corrupt head cannot authorize rebuild"

        let! after = image database
        Expect.equal after before "Head refusal preserves every stored byte"
    }

let private unsupportedBaseline database =
    task {
        let! _, checkpoint = seed database

        do!
            sql
                database
                "UPDATE claimcore.baseline_identity SET script_sha256=decode(repeat('00',32),'hex')"
                [||]

        let! before = image database
        let mutable refused = false

        try
            let! _ = ProjectionRebuild.rebuild database.OwnerSource checkpoint token in ()
        with :? InvalidOperationException ->
            refused <- true

        Expect.isTrue refused "Unsupported installation identity refuses before reconstruction"
        let! after = image database
        Expect.equal after before "Baseline refusal preserves identity and all data"
    }

let private stagingFailure database =
    task {
        let! _, checkpoint = seed database
        do! sql database "UPDATE claimcore.cases SET status='CLOSED'" [||]
        do! trigger database "RAISE EXCEPTION 'synthetic rebuild staging refusal';"
        let! before = image database
        let mutable refused = false

        try
            let! _ = ProjectionRebuild.rebuild database.OwnerSource checkpoint token in ()
        with :? PostgresException as error ->
            refused <- error.SqlState = "P0001"

        Expect.isTrue refused "The real replacement INSERT is interrupted after staged DELETE"
        let! after = image database

        Expect.equal
            after
            before
            "Staging failure rolls back every replacement and preserves damaged evidence"
    }

let private validationFailure database =
    task {
        let! _, checkpoint = seed database
        do! trigger database "NEW.status := 'CLOSED';"
        let! before = image database
        let! result = ProjectionRebuild.rebuild database.OwnerSource checkpoint token

        match result with
        | Error finding ->
            Expect.equal
                finding.Finding
                VerificationFinding.CaseStatusProjection
                "Post-replacement validation rejects real trigger-induced divergence"
        | Ok _ -> failtest "A malformed staged projection was committed"

        let! after = image database
        Expect.equal after before "Validation refusal rolls back the complete staged replacement"
    }

let private cancelledStaging database =
    task {
        let! _, checkpoint = seed database
        do! trigger database "PERFORM pg_advisory_xact_lock(734113);"
        let! before = image database
        use! blocker = database.OwnerSource.OpenConnectionAsync()
        use! held = blocker.BeginTransactionAsync()

        let! _ =
            DatabaseSession.rows blocker "SELECT pg_advisory_xact_lock(734113)" [||] token

        use cancelled = new CancellationTokenSource()

        let rebuilding =
            ProjectionRebuild.rebuild database.OwnerSource checkpoint cancelled.Token

        do! waitFor database advisoryWait
        cancelled.Cancel()
        let mutable refused = false

        try
            let! _ = rebuilding in ()
        with :? OperationCanceledException ->
            refused <- true

        do! held.RollbackAsync token
        Expect.isTrue refused "Real blocked replacement cancellation must propagate before commit"
        let! after = image database
        Expect.equal after before "Cancellation leaves no partially replaced projection"
    }

let private writerRace database =
    task {
        let! actor, checkpoint = seed database
        do! trigger database "PERFORM pg_advisory_xact_lock(734114);"
        use! blocker = database.OwnerSource.OpenConnectionAsync()
        use! held = blocker.BeginTransactionAsync()

        let! _ =
            DatabaseSession.rows blocker "SELECT pg_advisory_xact_lock(734114)" [||] token

        let rebuilding = ProjectionRebuild.rebuild database.OwnerSource checkpoint token
        do! waitFor database advisoryWait

        let writing =
            Service.execute
                actor
                (Inputs.caseOperation (
                    request 990 "AFTER-REBUILD" 0L (AuthoredCaseAct.Open registration)
                ))
                token

        do!
            waitFor
                database
                "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND usename='claimcore_runtime' AND wait_event_type='Lock')"

        Expect.isFalse writing.IsCompleted "An actor cannot pass the head lock during replacement"
        do! held.RollbackAsync token
        let! rebuilt = rebuilding

        Expect.equal
            (rebuilt |> Result.map _.Position)
            (Ok checkpoint)
            "Reconstruction used its locked checkpoint"

        let! appended = writing

        Expect.equal
            appended.Outcome
            (ExecuteResult.Rejected Refusal.RegisterMaintenance)
            "A queued actor must observe the maintenance state after replacement"

        let! after = verified database

        Expect.equal
            after
            checkpoint
            "A rebuild cannot let a queued actor append during maintenance"
    }

let private commitLoss drop landed database =
    task {
        let! _, checkpoint = seed database
        do! sql database "UPDATE claimcore.cases SET status='CLOSED'" [||]
        let! before = image database

        let settings =
            NpgsqlConnectionStringBuilder(database.Container.GetConnectionString())

        use proxy = new CommitProxy.Proxy(nonNull settings.Host, settings.Port, drop)
        settings.Host <- "127.0.0.1"
        settings.Port <- proxy.Port
        settings.SslMode <- SslMode.Disable
        settings.GssEncryptionMode <- GssEncryptionMode.Disable
        settings.Pooling <- false
        use source = NpgsqlDataSource.Create settings.ConnectionString
        let mutable unknown = false

        try
            let! _ = ProjectionRebuild.rebuild source checkpoint token in ()
        with :? AdministrationOutcomeUnknownException ->
            unknown <- true

        Expect.isTrue
            unknown
            "Lost real commit confirmation cannot become a definite maintenance result"

        let! after = image database

        if landed then
            let! current = verified database
            Expect.equal current checkpoint "After-commit loss leaves a complete valid replacement"
        else
            Expect.equal after before "Before-commit loss leaves the complete previous state"

        for table in [ "ledger"; "ledger_head"; "sessions" ] do
            Expect.equal
                ((after |> List.find (fst >> (=) table) |> snd))
                ((before |> List.find (fst >> (=) table) |> snd))
                "Commit uncertainty never fabricates or rewrites authoritative or session state"
    }

[<Tests>]
let tests =
    testList
        "projection reconstruction failures"
        [
            testCaseAsync
                "corrupt authoritative history refuses without byte changes"
                (run (
                    corruptHistory
                        "UPDATE claimcore.ledger SET entry_hash=decode(repeat('00',32),'hex') WHERE sequence=1"
                ))
            testCaseAsync
                "corrupt payload commitment refuses without byte changes"
                (run (
                    corruptHistory
                        "UPDATE claimcore.ledger SET payload_commitment=decode(repeat('00',32),'hex') WHERE sequence=1"
                ))
            testCaseAsync
                "corrupt retained creation reference refuses without byte changes"
                (run (
                    corruptHistory
                        "UPDATE claimcore.ledger SET creation_reference='WRONG-RETAINED-REFERENCE' WHERE act='OPEN'"
                ))
            testCaseAsync
                "corrupt recovery control refuses without byte changes"
                (run (
                    corruptHistory
                        "UPDATE claimcore.ledger SET payload=set_byte(payload,0,get_byte(payload,0)#1) WHERE subject_kind='CONTROL'"
                ))
            testCaseAsync "corrupt head refuses without byte changes" (run corruptHead)
            testCaseAsync
                "unsupported baseline refuses without byte changes"
                (run unsupportedBaseline)
            testCaseAsync "staging failure rolls back all replacements" (run stagingFailure)
            testCaseAsync
                "staged validation failure rolls back all replacements"
                (run validationFailure)
            testCaseAsync
                "cancellation during replacement rolls back all staged effects"
                (run cancelledStaging)
            testCaseAsync
                "the head lock excludes a writer throughout the rebuild scan and replacement"
                (run writerRace)
            testCaseAsync
                "before-commit acknowledgement loss remains uncertain and atomic"
                (run (commitLoss CommitProxy.Drop.BeforeCommit false))
            testCaseAsync
                "after-commit acknowledgement loss remains uncertain and atomic"
                (run (commitLoss CommitProxy.Drop.AfterCommit true))
        ]
