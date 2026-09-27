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
open ClaimCore.IntegrationTests.ActorGrantTestSupport

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
                    55L
                    "Current baseline includes actor authority, physical backup, copy custody, and erasure evidence"))

let private grantSeedRoles admin app witness principal =
    provision admin witness principal |> applied
    use source = RuntimeDataSource.create app
    let grants = new ActorGrantStore(source)
    let registry = new ActorGrantRegistry(source, witness)

    for role in [ Role.CaseEditor; Role.RecoveryOperator ] do
        registry.SetGrant(
            principal,
            actorId grants principal,
            {
                Role = role
                Scope = GrantScope.Installation
            },
            true
        )
        |> await
        |> applied

let private seedRetainedCase admin app writer witness =
    let principal = human "baseline-owner"
    grantSeedRoles admin app witness principal

    let result =
        use runtime =
            Runtime.OpenPostgres(
                app,
                writer,
                witnessKey (),
                suppressionKeyFile (),
                artifactKeyRingFile (),
                CancellationToken.None
            )
            |> await
            |> accepted

        let core = runtime.ForActor principal
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
        | ResolveOutcome.ResolveCompleted(_, _, DefiniteExecution.Accepted _, _) -> ()
        | _ -> failtest "Expected accepted retained operation."

        let revokedId = Guid.NewGuid()
        let revoked = openRequest revokedId ("BASELINE-REVOKED-" + revokedId.ToString("N"))

        let revokedDigest =
            match core.Prepare(revoked, CancellationToken.None) |> await with
            | PrepareOutcome.Prepared(details, _) ->
                details.Summary.RequestSha256
                |> Option.defaultWith (fun () -> failtest "Expected retained revocation digest.")
            | _ -> failtest "Expected revocable preparation."

        match
            core.Recovery.Dismiss(revokedId, revokedDigest, true, CancellationToken.None)
            |> await
        with
        | RecoveryDismissOutcome.DismissedPreparation _ -> operationId
        | _ -> failtest "Expected witnessed revocation."

    result

let private repeat =
    testCase
        "[CC-DB-001] identical initialization and read-only verification preserve every stored byte"
        (fun () ->
            withAuthorityRuntimeDatabase (fun admin app writer witness ->
                seedRetainedCase admin app writer witness |> ignore

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
                SchemaBaseline.initialize admin "Europe/Riga" syntheticSuppressionCheck
                |> completedAdministration

                let before = snapshot admin

                SchemaBaseline.initialize admin "Etc/UTC" syntheticSuppressionCheck
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
                            Task.Run(fun () ->
                                SchemaBaseline.initialize
                                    admin
                                    "Etc/UTC"
                                    syntheticSuppressionCheck)
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
                        Task.Run(fun () ->
                            SchemaBaseline.initialize admin zone syntheticSuppressionCheck)
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

                SchemaBaseline.initialize admin "Etc/UTC" syntheticSuppressionCheck
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
