module ClaimCore.IntegrationTests.BaselineRefusalTests

open Expecto
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
                    "CREATE SCHEMA claimcore; CREATE TABLE claimcore.sentinel (evidence bytea); INSERT INTO claimcore.sentinel VALUES ('\x000102ff')"

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

                    SchemaBaseline.initialize admin "Etc/UTC"
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

                SchemaBaseline.initialize admin "Etc/UTC"
                |> refusedAdministration AdministrationFailure.SchemaDefinitionInvalid

                runtimeRefuses app
                Expect.equal (snapshot admin) before "No automatic constraint repair"))

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
        ]
