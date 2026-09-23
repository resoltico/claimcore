module ClaimCore.IntegrationTests.FreshBaselineTests

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open Expecto
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.FreshBaselineSupport

let private installed =
    testCase
        "[CC-DB-001] fresh initialization atomically installs the current identity and calendar"
        (fun () ->
            withDatabase (fun admin app ->
                initialize admin
                SchemaBaseline.verify admin |> completedAdministration
                use connection = new NpgsqlConnection(app)
                connection.Open()
                RuntimeSchema.requireCompatible connection
                RuntimeAcl.requireRole connection
                RuntimeAcl.requireAcl connection
                let expected = SchemaDefinition.current ()

                Expect.equal
                    (scalar admin "SELECT baseline_id FROM claimcore.schema_baseline" :?> string)
                    expected.Id
                    "Baseline identity"

                Expect.equal
                    (scalar admin "SELECT script_sha256 FROM claimcore.schema_baseline" :?> string)
                    expected.Digest
                    "Frozen source digest"

                Expect.equal
                    (scalar admin "SELECT business_time_zone FROM claimcore.installation_lineage"
                    :?> string)
                    "Etc/UTC"
                    "Explicit calendar"

                Expect.notEqual
                    (scalar admin "SELECT lineage_id FROM claimcore.installation_lineage" :?> Guid)
                    Guid.Empty
                    "Fresh nonempty installation identity"

                Expect.equal
                    (scalar
                        admin
                        "SELECT count(*) FROM information_schema.tables WHERE table_schema='claimcore' AND table_type='BASE TABLE'"
                    :?> int64)
                    10L
                    "Current storage only, including owner-only prune journal"))

let private seedRetainedCase app =
    use runtime = Runtime.OpenPostgres(app, CancellationToken.None) |> await |> accepted
    let core = runtime.Core
    let operationId = Guid.NewGuid()

    let request =
        openRequest operationId ("BASELINE-REPEAT-" + operationId.ToString("N"))

    let digest =
        match core.Prepare(request, CancellationToken.None) |> await with
        | PrepareOutcome.Prepared(details, _) ->
            details.Summary.RequestSha256
            |> Option.defaultWith (fun () -> failtest "Expected retained digest.")
        | _ -> failtest "Expected current preparation."

    match core.Recovery.Resolve(operationId, digest, CancellationToken.None) |> await with
    | ResolveOutcome.ResolveCompleted(_, _, DefiniteExecution.Accepted _, _) -> operationId
    | _ -> failtest "Expected accepted retained operation."

let private repeat =
    testCase
        "[CC-DB-001] identical initialization and read-only verification preserve every stored byte"
        (fun () ->
            withDatabase (fun admin app ->
                initialize admin
                let acceptedOperation = seedRetainedCase app

                execute
                    admin
                    ("INSERT INTO claimcore.request_submission_attempts (attempt_id, operation_id) VALUES (gen_random_uuid(), '"
                     + acceptedOperation.ToString("D")
                     + "')")

                execute
                    admin
                    "INSERT INTO claimcore.operation_revocations VALUES ('00000000-0000-4000-8000-000000000001',3,repeat('a',64),TIMESTAMPTZ '2020-01-01 00:00:00+00','OPERATOR_DISMISSAL')"

                let before = snapshot admin
                initialize admin
                SchemaBaseline.verify admin |> completedAdministration

                Expect.equal
                    (snapshot admin)
                    before
                    "No marker timestamp, lineage, tombstone or DDL change"))

let private absent =
    testCase
        "[CC-DB-001] verification and maintenance refuse an absent baseline without creating it"
        (fun () ->
            withDatabase (fun admin app ->
                SchemaBaseline.verify admin
                |> refusedAdministration AdministrationFailure.BaselineMissing

                PreparationPruning.prune admin PreparationPruneOptions.defaults
                |> refusedAdministration AdministrationFailure.BaselineMissing

                runtimeRefuses app

                Expect.equal
                    (scalar admin "SELECT count(*) FROM pg_namespace WHERE nspname='claimcore'"
                    :?> int64)
                    0L
                    "Read and maintenance never initialize"))

let private calendar =
    testCase
        "[CC-DB-001] a different calendar is refused without rewriting installation identity"
        (fun () ->
            withDatabase (fun admin _ ->
                SchemaBaseline.initialize admin "Europe/Riga" |> completedAdministration
                let before = snapshot admin

                SchemaBaseline.initialize admin "Etc/UTC"
                |> refusedAdministration AdministrationFailure.BusinessZoneAlreadyConfigured

                Expect.equal (snapshot admin) before "First valid calendar remains immutable"))

let private concurrentSame =
    testCase
        "[CC-DB-001] concurrent identical initializers serialize to one complete installation"
        (fun () ->
            withDatabase (fun admin _ ->
                let tasks =
                    [|
                        for _ in 1..4 ->
                            Task.Run(fun () -> SchemaBaseline.initialize admin "Etc/UTC")
                    |]

                Task.WhenAll(tasks) |> await |> Array.iter completedAdministration
                SchemaBaseline.verify admin |> completedAdministration

                Expect.equal
                    (scalar admin "SELECT count(*) FROM claimcore.installation_lineage" :?> int64)
                    1L
                    "One lineage"

                Expect.equal
                    (scalar admin "SELECT count(*) FROM claimcore.schema_baseline" :?> int64)
                    1L
                    "One baseline"))

let private concurrentDifferent =
    testCase "[CC-DB-001] concurrent different calendars cannot both win initialization" (fun () ->
        withDatabase (fun admin _ ->
            let tasks =
                [|
                    for zone in [ "Etc/UTC"; "Europe/Riga" ] ->
                        Task.Run(fun () -> SchemaBaseline.initialize admin zone)
                |]

            let outcomes = Task.WhenAll(tasks) |> await

            let success =
                outcomes
                |> Array.filter (function
                    | AdministrationOutcome.Completed _ -> true
                    | _ -> false)

            Expect.equal success.Length 1 "One initializer wins"

            outcomes
            |> Array.iter (function
                | AdministrationOutcome.Completed _ -> ()
                | other ->
                    refusedAdministration
                        AdministrationFailure.BusinessZoneAlreadyConfigured
                        other)

            SchemaBaseline.verify admin |> completedAdministration))

let private rollback =
    testCase
        "[CC-DB-001] baseline DDL failure rolls back the entire namespace before commit"
        (fun () ->
            withDatabase (fun admin _ ->
                execute
                    admin
                    """
                CREATE FUNCTION public.reject_baseline_end() RETURNS event_trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_event_trigger_ddl_commands()
                        WHERE object_identity='claimcore.operation_revocations') THEN
                        RAISE EXCEPTION 'synthetic baseline failure';
                    END IF;
                END $$;
                CREATE EVENT TRIGGER baseline_failure ON ddl_command_end
                    WHEN TAG IN ('CREATE TABLE') EXECUTE FUNCTION public.reject_baseline_end();
                """

                SchemaBaseline.initialize admin "Etc/UTC"
                |> refusedAdministration AdministrationFailure.DatabaseUnavailable

                Expect.equal
                    (scalar admin "SELECT count(*) FROM pg_namespace WHERE nspname='claimcore'"
                    :?> int64)
                    0L
                    "No partially committed tables, grants, marker or lineage"

                execute
                    admin
                    "DROP EVENT TRIGGER baseline_failure; DROP FUNCTION public.reject_baseline_end()"

                initialize admin))

let tests =
    testList
        "fresh installation qualification"
        [
            installed
            repeat
            absent
            calendar
            concurrentSame
            concurrentDifferent
            rollback
        ]
