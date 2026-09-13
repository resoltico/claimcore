module ClaimCore.IntegrationTests.MigrationUpgradeSupport

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open NpgsqlTypes
open Expecto
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures

let private execute connectionString sql =
    use connection = new NpgsqlConnection(connectionString)
    connection.Open()
    use command = new NpgsqlCommand(sql, connection)
    command.ExecuteNonQuery() |> ignore

let private applyMigration version admin =
    let migration =
        SchemaDefinition.all () |> List.find (fun item -> item.Version = version)

    use connection = new NpgsqlConnection(admin)
    connection.Open()
    use transaction = connection.BeginTransaction()
    use command = new NpgsqlCommand(migration.Script, connection, transaction)
    command.ExecuteNonQuery() |> ignore

    use record =
        new NpgsqlCommand(
            "INSERT INTO claimcore.schema_migrations (version, name, script_sha256) VALUES (@version, @name, @digest)",
            connection,
            transaction
        )

    record.Parameters.AddWithValue("version", migration.Version) |> ignore
    record.Parameters.AddWithValue("name", migration.Name) |> ignore
    record.Parameters.AddWithValue("digest", migration.Digest) |> ignore
    record.ExecuteNonQuery() |> ignore
    transaction.Commit()

let installMigrationTwo admin = applyMigration 2 admin
let installMigrationThree admin = applyMigration 3 admin
let installMigrationFour admin = applyMigration 4 admin

let insertLegacyStarted (admin: string) =
    let request = newRequest ()
    let canonical = RequestRecord.encode request
    use connection = new NpgsqlConnection(admin)
    connection.Open()
    use transaction = connection.BeginTransaction()

    use preparation =
        new NpgsqlCommand(
            "INSERT INTO claimcore.request_preparations (operation_id, protocol_version, request_sha256, canonical_request, prepared_at, prepared_application_version, web_contract_fingerprint) VALUES (@operation, @protocol, @digest, @request, TIMESTAMPTZ '2020-01-01 00:00:00+00', @application, repeat('a', 64))",
            connection,
            transaction
        )

    Sql.uuid preparation "operation" request.OperationId

    Sql.add
        preparation
        "protocol"
        NpgsqlDbType.Smallint
        (box (int16 RecordVersions.CanonicalCommandFormat))

    Sql.text preparation "digest" (canonical |> SHA256.HashData |> Convert.ToHexStringLower)
    Sql.add preparation "request" NpgsqlDbType.Bytea (box canonical)
    Sql.text preparation "application" BuildIdentity.current.Version
    preparation.ExecuteNonQuery() |> ignore

    use lifecycle =
        new NpgsqlCommand(
            "INSERT INTO claimcore.request_preparation_lifecycle (operation_id, state, recorded_at) VALUES (@operation, 'SUBMISSION_STARTED', TIMESTAMPTZ '2020-01-01 00:00:00+00')",
            connection,
            transaction
        )

    Sql.uuid lifecycle "operation" request.OperationId
    lifecycle.ExecuteNonQuery() |> ignore
    transaction.Commit()
    request.OperationId, canonical |> SHA256.HashData |> Convert.ToHexStringLower

let private resolveThroughPublicCore (app: string) operationId requestSha256 =
    let runtime =
        Runtime.OpenPostgres(app, CancellationToken.None)
        |> await
        |> Result.defaultWith (fun _ -> failtest "Upgraded runtime must open.")

    use lifetime = runtime

    match
        lifetime.Core.Recovery.Resolve(operationId, requestSha256, CancellationToken.None)
        |> await
    with
    | ResolveOutcome.ResolveCompleted _
    | ResolveOutcome.ResolveObservedAccepted _ -> ()
    | _ -> failtest "Legacy started preparation must resolve through the public recovery workflow."

let private assertLegacyProvenance admin operationId =
    use connection = new NpgsqlConnection(admin)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT canonical_request_format, preparing_contract_fingerprint, preparing_contract_kind "
            + "FROM claimcore.request_preparations WHERE operation_id = @operation",
            connection
        )

    Sql.uuid command "operation" operationId
    use reader = command.ExecuteReader()
    Expect.isTrue (reader.Read()) "Legacy preparation survives provenance migration"

    Expect.equal
        (reader.GetInt16(0), reader.GetString(1), reader.GetString(2))
        (int16 RecordVersions.CanonicalCommandFormat, String.replicate 64 "a", "LEGACY_UNCLASSIFIED")
        "Migration preserves opaque legacy provenance without reinterpretation"

let private assertRetained admin operationId =
    let prune =
        PreparationPruning.prune
            admin
            { PreparationPruneOptions.defaults with
                SettledRetentionDays = 1
                AbandonedRetentionDays = 1
            }

    Expect.equal prune.DeletedCount 0 "Pre-003 uncertainty is never inferred away"
    use connection = new NpgsqlConnection(admin)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT count(*) FROM claimcore.request_preparations WHERE operation_id = @operation",
            connection
        )

    Sql.uuid command "operation" operationId
    Expect.equal (command.ExecuteScalar() :?> int64) 1L "Pre-003 preparation remains unresolved"

let verifyLegacyUncertainty admin app =
    installMigrationTwo admin
    let legacyStarted, digest = insertLegacyStarted admin
    installMigrationThree admin
    Migrations.apply admin
    Migrations.apply admin
    assertLegacyProvenance admin legacyStarted
    resolveThroughPublicCore app legacyStarted digest

    execute
        admin
        "UPDATE claimcore.request_submission_attempts SET started_at = TIMESTAMPTZ '2020-01-01 00:00:00+00'; UPDATE claimcore.request_submission_settlements SET recorded_at = TIMESTAMPTZ '2020-01-01 00:00:00+00'"

    assertRetained admin legacyStarted
