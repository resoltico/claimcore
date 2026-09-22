module ClaimCore.IntegrationTests.MigrationUpgradeTests

open System
open System.Security.Cryptography
open System.Text
open Npgsql
open NpgsqlTypes
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.IntegrationTests.Fixtures

let private quoteIdentifier value =
    use builder = new NpgsqlCommandBuilder()
    builder.QuoteIdentifier(value)

let private connectionFor database (source: string) =
    let builder = NpgsqlConnectionStringBuilder(source)
    builder.Database <- database
    builder.ConnectionString

let private execute connectionString sql =
    use connection = new NpgsqlConnection(connectionString)
    connection.Open()
    use command = new NpgsqlCommand(sql, connection)
    command.ExecuteNonQuery() |> ignore

let private migrationOne _database admin =
    let first = SchemaDefinition.all () |> List.head
    use connection = new NpgsqlConnection(admin)
    connection.Open()
    use transaction = connection.BeginTransaction()

    use bootstrap =
        new NpgsqlCommand(
            """
            CREATE SCHEMA claimcore;
            REVOKE ALL ON SCHEMA claimcore FROM PUBLIC;
            CREATE TABLE claimcore.schema_migrations (
                version integer PRIMARY KEY CHECK (version > 0),
                name text NOT NULL UNIQUE CHECK (name ~ '^[0-9]{3}_[a-z0-9_]+$'),
                script_sha256 text NOT NULL CHECK (script_sha256 ~ '^[0-9a-f]{64}$'),
                applied_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                applied_by text NOT NULL DEFAULT session_user
            );
            REVOKE ALL ON TABLE claimcore.schema_migrations FROM PUBLIC;
            """
            + first.Script,
            connection,
            transaction
        )

    bootstrap.ExecuteNonQuery() |> ignore

    use record =
        new NpgsqlCommand(
            "INSERT INTO claimcore.schema_migrations (version, name, script_sha256) VALUES (1, @name, @digest)",
            connection,
            transaction
        )

    record.Parameters.AddWithValue("name", first.Name) |> ignore
    record.Parameters.AddWithValue("digest", first.Digest) |> ignore
    record.ExecuteNonQuery() |> ignore
    transaction.Commit()

let private insertSyntheticHistory app =
    let request =
        { newRequest () with
            CaseReference = "UPGRADE-" + Guid.NewGuid().ToString("N")
        }

    let claim = Claim.decide (DateOnly(2026, 9, 7)) request None |> accepted
    let view = Claim.view claim
    use connection = new NpgsqlConnection(app)
    connection.Open()
    use transaction = connection.BeginTransaction()
    use caseInsert = new NpgsqlCommand(Sql.insertCase, connection, transaction)
    Rows.bindClaim caseInsert claim
    Sql.integer caseInsert "revision" view.Version
    caseInsert.ExecuteNonQuery() |> ignore

    use history =
        new NpgsqlCommand(
            """
            INSERT INTO claimcore.case_changes (
                operation_id, case_reference, revision, command_name, request_format_version,
                request_sha256, snapshot_version, snapshot
            ) VALUES (@operation, @reference, @revision, 'OPEN', 1, @digest, 2, @snapshot)
            """,
            connection,
            transaction
        )

    Sql.uuid history "operation" request.OperationId
    Sql.text history "reference" request.CaseReference
    Sql.integer history "revision" view.Version

    Sql.text
        history
        "digest"
        (RequestRecord.encode request |> SHA256.HashData |> Convert.ToHexStringLower)

    Sql.add
        history
        "snapshot"
        NpgsqlDbType.Jsonb
        (box (view |> CaseRecord.encodeSnapshot |> Encoding.UTF8.GetString))

    history.ExecuteNonQuery() |> ignore
    transaction.Commit()
    request

let private retainedShape connectionString reference =
    use connection = new NpgsqlConnection(connectionString)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT revision, request_sha256, snapshot::text FROM claimcore.case_changes WHERE case_reference = @reference",
            connection
        )

    command.Parameters.AddWithValue("reference", reference) |> ignore
    use reader = command.ExecuteReader()
    Expect.isTrue (reader.Read()) "Synthetic migration-001 receipt exists"
    reader.GetInt64(0), reader.GetString(1), reader.GetString(2)

let withMigrationOne action =
    let database = "upgrade_" + Guid.NewGuid().ToString("N").Substring(0, 16) + "_test"
    let adminRoot = adminConnection ()
    let admin = connectionFor database adminRoot
    let app = connectionFor database (appConnection ())
    let quoted = quoteIdentifier database
    execute adminRoot ("CREATE DATABASE " + quoted)

    try
        execute
            adminRoot
            ("REVOKE ALL ON DATABASE "
             + quoted
             + " FROM PUBLIC; GRANT CONNECT ON DATABASE "
             + quoted
             + " TO claimcore_app")

        migrationOne database admin
        action admin app
    finally
        NpgsqlConnection.ClearAllPools()
        execute adminRoot ("DROP DATABASE IF EXISTS " + quoted)

let private retentionUpgradeTests =
    testList
        "retained data"
        [
            testCase
                "[CC-DB-001] upgrade preserves cases and history and installs recovery storage"
                (fun () ->
                    withMigrationOne (fun admin app ->
                        let request = insertSyntheticHistory app
                        let before = retainedShape app request.CaseReference
                        MigrationUpgradeSupport.verifyLegacyUncertainty admin app

                        let after = retainedShape app request.CaseReference

                        Expect.isTrue
                            (after = before)
                            "Migration leaves accepted history byte-for-byte projection intact"

                        use runtime = new PostgresStore(app)

                        Expect.equal
                            (runtime.CheckSchema() |> await)
                            (Ok())
                            "Upgraded runtime admits exact manifest"

                        let current =
                            (runtime :> IClaimStore).Get(request.CaseReference)
                            |> await
                            |> accepted

                        Expect.isSome current "All thirteen stored fields remain readable"))
        ]

let private atomicityUpgradeTests =
    testList
        "atomicity"
        [
            testCase
                "[CC-DB-001] failed migration rolls back its objects and journal row"
                (fun () ->
                    withMigrationOne (fun admin _ ->
                        MigrationUpgradeSupport.installMigrationTwo admin
                        MigrationUpgradeSupport.installMigrationThree admin

                        execute
                            admin
                            "ALTER TABLE claimcore.request_preparations ADD COLUMN preparing_contract_kind text"

                        Migrations.apply admin
                        |> refusedAdministration AdministrationFailure.DatabaseUnavailable

                        use connection = new NpgsqlConnection(admin)
                        connection.Open()

                        use command =
                            new NpgsqlCommand(
                                "SELECT EXISTS (SELECT 1 FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'claimcore' AND c.relname = 'request_preparations' AND a.attname = 'protocol_version'), EXISTS (SELECT 1 FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'claimcore' AND c.relname = 'request_preparations' AND a.attname = 'web_contract_fingerprint'), EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'preparing_contract_kind_value'), count(*) FROM claimcore.schema_migrations",
                                connection
                            )

                        use reader = command.ExecuteReader()
                        Expect.isTrue (reader.Read()) "Migration journal remains readable"

                        Expect.equal
                            (reader.GetBoolean(0),
                             reader.GetBoolean(1),
                             reader.GetBoolean(2),
                             reader.GetInt64(3))
                            (true, true, false, 3L)
                            "No partial migration-004 state"))
        ]

let private manifestUpgradeTests =
    testList
        "manifest validation"
        [
            testCase "[CC-DB-001] applied migration identities remain frozen" (fun () ->
                let actual =
                    SchemaDefinition.all ()
                    |> List.map (fun item -> item.Version, item.Name, item.Digest)

                Expect.sequenceEqual
                    actual
                    [
                        1,
                        "001_initial",
                        "9b815a08f2a0a6b9d697a7eb48054aa57e5518b244b4216d0824b9e8d2a33f1d"
                        2,
                        "002_request_preparations",
                        "b80387adbc060f3a2a53e29b474c6893fc086d891b60cfdf8bbd9342fc7dc2cc"
                        3,
                        "003_submission_attempts",
                        "d864d67ef6cba3272d575fdec8d2daf7d19df13ea938758364e261cd43ba8ad2"
                        4,
                        "004_generalize_preparation_provenance",
                        "2133e8da7d7df9f53a99bfa132dcde780af9e71ffdc15d35d9c0949cb3e3aa8c"
                        5,
                        "005_recovery_evidence_read_acl",
                        "48652349e683df4f94e4dc2fcf27f40c2a35d69ba0ee7007871f616b79ce783b"
                        6,
                        "006_operation_authority_and_business_time",
                        "b73ea23977478361bd708aef0fa357cf24d4af6ea096bfa3ff225710cb722818"
                    ]
                    "Applied migration names and bytes are immutable")
            testCase "[CC-DB-001] migrator refuses a newer manifest entry" (fun () ->
                withMigrationOne (fun admin _ ->
                    Migrations.apply admin |> completedAdministration

                    execute
                        admin
                        "INSERT INTO claimcore.schema_migrations (version, name, script_sha256) VALUES (7, '007_future', repeat('0', 64))"

                    Migrations.apply admin
                    |> refusedAdministration AdministrationFailure.MigrationNewerThanRuntime))
        ]

let private upgradeTests =
    testList
        "migration 001 through 006"
        [ retentionUpgradeTests; atomicityUpgradeTests; manifestUpgradeTests ]

let tests = testList "PostgreSQL migration upgrade qualification" [ upgradeTests ]
