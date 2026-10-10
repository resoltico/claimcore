module ClaimCore.IntegrationTests.BaselineRefusalTests

open Expecto
open System
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.FreshBaselineSupport

let private historical =
    testCase
        "[CC-DB-001] all old installation markers are refused without touching evidence or history"
        (fun () ->
            for version in 1..6 do
                withDatabase (fun admin app ->
                    // Negative sentinels model old storage identity, not a retained upgrade implementation.
                    execute
                        admin
                        ("""
                    CREATE SCHEMA claimcore;
                    CREATE TABLE claimcore.schema_migrations (version integer, name text, script_sha256 text);
                    INSERT INTO claimcore.schema_migrations VALUES (
                    """
                         + string version
                         + ", 'historical', repeat('a',64));"
                         + """
                    CREATE TABLE claimcore.cases (evidence bytea);
                    CREATE TABLE claimcore.case_changes (evidence bytea);
                    CREATE TABLE claimcore.request_preparations (evidence bytea);
                    CREATE TABLE claimcore.request_submission_attempts (evidence bytea);
                    CREATE TABLE claimcore.request_submission_settlements (evidence bytea);
                    CREATE TABLE claimcore.operation_revocations (evidence bytea);
                    INSERT INTO claimcore.cases VALUES ('\x000102ff');
                    INSERT INTO claimcore.case_changes VALUES ('\x0304fe');
                    INSERT INTO claimcore.request_preparations VALUES ('\x0506fd');
                    INSERT INTO claimcore.request_submission_attempts VALUES ('\x0708fc');
                    INSERT INTO claimcore.request_submission_settlements VALUES ('\x090afb');
                    INSERT INTO claimcore.operation_revocations VALUES ('\x0b0cfa');
                    """)

                    assertUnsupported admin app))

let private emptyNamespace =
    testCase "[CC-DB-001] even an empty pre-existing namespace is not silently adopted" (fun () ->
        withDatabase (fun admin app ->
            execute admin "CREATE SCHEMA claimcore"
            assertUnsupported admin app))

let private occupied =
    testCase
        "[CC-DB-001] an unmarked occupied namespace is refused without deleting its contents"
        (fun () ->
            withDatabase (fun admin app ->
                execute
                    admin
                    "CREATE SCHEMA claimcore; CREATE TABLE claimcore.sentinel (evidence bytea); INSERT INTO claimcore.sentinel VALUES (decode('000102ff', 'hex'))"

                assertUnsupported admin app))

let private missingMarker =
    testCase
        "[CC-DB-001] current-looking tables without a baseline marker remain unsupported"
        (fun () ->
            withDatabase (fun admin app ->
                initialize admin
                execute admin "DROP TABLE claimcore.schema_baseline"
                assertUnsupported admin app))

let private mixed =
    testCase
        "[CC-DB-001] mixing a current marker with historical metadata cannot authorize old storage"
        (fun () ->
            withDatabase (fun admin app ->
                initialize admin

                execute
                    admin
                    "CREATE TABLE claimcore.schema_migrations (version integer); INSERT INTO claimcore.schema_migrations VALUES (6)"

                assertUnsupported admin app))

let private malformedMarker =
    testCase
        "[CC-DB-001] malformed or executable baseline lookalikes are not queried or repaired"
        (fun () ->
            for definition in
                [
                    "CREATE VIEW claimcore.schema_baseline AS SELECT true singleton"
                    "CREATE TABLE claimcore.schema_baseline (singleton boolean)"
                ] do
                withDatabase (fun admin app ->
                    execute admin ("CREATE SCHEMA claimcore; " + definition)
                    assertUnsupported admin app))

let private corruptIdentity =
    testCase
        "[CC-DB-001] unknown identities and altered digests are refused with no repair"
        (fun () ->
            for mutation in
                [
                    "UPDATE claimcore.schema_baseline SET baseline_id='unknown-future-1'"
                    "UPDATE claimcore.schema_baseline SET script_sha256=repeat('0',64)"
                    "DELETE FROM claimcore.schema_baseline"
                ] do
                withDatabase (fun admin app ->
                    initialize admin
                    execute admin mutation
                    let before = snapshot admin

                    SchemaBaseline.initialize admin "Etc/UTC" syntheticSuppressionCheck
                    |> refusedAdministration AdministrationFailure.BaselineIdentityMismatch

                    SchemaBaseline.verify admin
                    |> refusedAdministration AdministrationFailure.BaselineIdentityMismatch

                    PreparationPruning.prune admin PreparationPruneOptions.defaults
                    |> refusedAdministration AdministrationFailure.BaselineIdentityMismatch

                    runtimeRefuses app
                    Expect.equal (snapshot admin) before "No marker repair or data mutation"))

let private incomplete =
    testCase
        "[CC-DB-001] a matching marker does not conceal missing current integrity constraints"
        (fun () ->
            withDatabase (fun admin app ->
                initialize admin

                execute
                    admin
                    "ALTER TABLE claimcore.operation_revocations DROP CONSTRAINT operation_revocations_canonical_request_format_check"

                let before = snapshot admin

                SchemaBaseline.initialize admin "Etc/UTC" syntheticSuppressionCheck
                |> refusedAdministration AdministrationFailure.SchemaDefinitionInvalid

                runtimeRefuses app
                Expect.equal (snapshot admin) before "No automatic constraint repair"))

let private actualCleanupFailure () =
    let original = InvalidOperationException("PRIVATE-BODY")
    let mutable observed: exn option = None

    try
        withDatabase (fun admin _ ->
            let database =
                Npgsql.NpgsqlConnectionStringBuilder(admin).Database
                |> Option.ofObj
                |> Option.defaultWith (fun () ->
                    failtest "Owned synthetic database identity is absent.")

            use builder = new Npgsql.NpgsqlCommandBuilder()
            Npgsql.NpgsqlConnection.ClearAllPools()
            execute (adminConnection ()) ("DROP DATABASE " + builder.QuoteIdentifier(database))
            raise original)
    with error ->
        observed <- Some error

    Expect.isTrue
        (observed |> Option.exists (fun error -> obj.ReferenceEquals(error, original)))
        "Actual missing-database cleanup cannot replace the original refusal"

    Expect.equal
        original.Data["FixtureCleanupFailure"]
        (box "postgres-refusal")
        "Actual fixed cleanup category"

    Expect.equal
        original.Data["FixtureCleanupSettlement"]
        (box "unknown")
        "No success inferred from cleanup refusal"

let private diagnosticRefusal () =
    let original =
        { new Exception("PRIVATE-BODY") with
            override _.Data =
                System.Collections.ObjectModel.ReadOnlyDictionary<string, obj>(
                    System.Collections.Generic.Dictionary<string, obj>()
                )
                :> System.Collections.IDictionary
        }

    let previous = Console.Error
    use output = new System.IO.StringWriter()
    let mutable observed: exn option = None

    try
        Console.SetError(output)

        try
            FixtureCleanup.run (fun () -> raise (TimeoutException("PRIVATE-CLEANUP"))) (fun () ->
                raise original)
        with error ->
            observed <- Some error
    finally
        Console.SetError(previous)

    Expect.isTrue
        (observed |> Option.exists (fun error -> obj.ReferenceEquals(error, original)))
        "Diagnostic refusal cannot replace the original failure"

    Expect.stringContains
        (output.ToString())
        "fixture-cleanup-settlement=unknown kind=timeout diagnostic-retention=unavailable"
        "Fixed fallback exposes cleanup uncertainty"

    Expect.isFalse
        ((output.ToString()).Contains("PRIVATE", StringComparison.Ordinal))
        "Fallback omits both provider messages"

let private cleanupFailures =
    testCase
        "[CC-DB-001] database cleanup preserves the original refusal and exposes uncertainty"
        (fun () ->
            let bodyError = InvalidOperationException("PRIVATE-BODY")
            let cleanupError = TimeoutException("PRIVATE-CLEANUP")

            let observed cleanup body =
                try
                    FixtureCleanup.run cleanup body |> ignore
                    None
                with error ->
                    Some error

            let doubled = observed (fun () -> raise cleanupError) (fun () -> raise bodyError)

            Expect.isTrue
                (doubled |> Option.exists (fun error -> obj.ReferenceEquals(error, bodyError)))
                "Body failure survives"

            Expect.equal
                bodyError.Data["FixtureCleanupFailure"]
                (box "timeout")
                "Closed cleanup kind"

            Expect.equal
                bodyError.Data["FixtureCleanupSettlement"]
                (box "unknown")
                "No cleanup settlement inferred"

            let single = observed (fun () -> raise cleanupError) (fun () -> ())

            Expect.isTrue
                (single |> Option.exists (fun error -> obj.ReferenceEquals(error, cleanupError)))
                "Cleanup failure after success refuses"

            Expect.equal
                (FixtureCleanup.run (fun () -> ()) (fun () -> 7))
                7
                "Settled body result survives"

            actualCleanupFailure ()
            diagnosticRefusal ())

let tests =
    testList
        "unsupported installation refusal"
        [
            historical
            emptyNamespace
            occupied
            missingMarker
            mixed
            malformedMarker
            corruptIdentity
            incomplete
            cleanupFailures
        ]
