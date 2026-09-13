module ClaimCore.IntegrationTests.MigrationEvidenceUpgradeTests

open System
open System.Threading
open Npgsql
open Expecto
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures

let private canonicalBytes admin operationId =
    use connection = new NpgsqlConnection(admin)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT canonical_request, request_sha256 FROM claimcore.request_preparations WHERE operation_id = @operation",
            connection
        )

    Sql.uuid command "operation" operationId
    use reader = command.ExecuteReader()
    Expect.isTrue (reader.Read()) "Synthetic legacy preparation exists"
    reader.GetFieldValue<byte array>(0), reader.GetString(1)

let private evidenceCounts admin operationId =
    use connection = new NpgsqlConnection(admin)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT (SELECT count(*) FROM claimcore.request_submission_legacy_uncertainty WHERE operation_id = @operation), "
            + "(SELECT count(*) FROM claimcore.request_submission_attempts WHERE operation_id = @operation), "
            + "(SELECT count(*) FROM claimcore.request_submission_settlements s JOIN claimcore.request_submission_attempts a ON a.attempt_id = s.attempt_id WHERE a.operation_id = @operation)",
            connection
        )

    Sql.uuid command "operation" operationId
    use reader = command.ExecuteReader()
    Expect.isTrue (reader.Read()) "Synthetic evidence counts are readable"
    reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)

let private seedDefiniteAttempt admin operationId =
    use connection = new NpgsqlConnection(admin)
    connection.Open()
    use transaction = connection.BeginTransaction()
    let attemptId = Guid.NewGuid()

    use attempt =
        new NpgsqlCommand(
            "INSERT INTO claimcore.request_submission_attempts (attempt_id, operation_id) VALUES (@attempt, @operation)",
            connection,
            transaction
        )

    Sql.uuid attempt "attempt" attemptId
    Sql.uuid attempt "operation" operationId
    Expect.equal (attempt.ExecuteNonQuery()) 1 "Synthetic attempt inserted"

    use settlement =
        new NpgsqlCommand(
            "INSERT INTO claimcore.request_submission_settlements (attempt_id, outcome) VALUES (@attempt, 'ERROR')",
            connection,
            transaction
        )

    Sql.uuid settlement "attempt" attemptId
    Expect.equal (settlement.ExecuteNonQuery()) 1 "Synthetic settlement inserted"
    transaction.Commit()

let private inspect app operationId =
    use runtime =
        Runtime.OpenPostgres(app, CancellationToken.None)
        |> await
        |> Result.defaultWith (fun _ -> failtest "Migrated runtime must open.")

    match runtime.Core.Recovery.Inspect(operationId, CancellationToken.None) |> await with
    | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found details) -> details
    | _ -> failtest "Migrated legacy preparation must be inspectable."

let private assertUpgraded app operationId expectedAttemptCount =
    let details = inspect app operationId
    Expect.isTrue details.Preparation.LegacyUncertainty "Pre-003 uncertainty is retained"

    Expect.equal
        details.Preparation.PreparingContractKind
        "LEGACY_UNCLASSIFIED"
        "Original provenance is not rewritten"

    Expect.equal
        details.Preparation.Attempts.Length
        expectedAttemptCount
        "Exact append-only attempts are projected"

    details.Preparation.Attempts
    |> List.iter (fun attempt ->
        Expect.equal attempt.Settlement (Some "ERROR") "Stored settlement preserved")

let private upgradePath startVersion label =
    testCase label (fun () ->
        MigrationUpgradeTests.withMigrationOne (fun admin app ->
            MigrationUpgradeSupport.installMigrationTwo admin
            let operationId, digest = MigrationUpgradeSupport.insertLegacyStarted admin

            if startVersion >= 3 then
                MigrationUpgradeSupport.installMigrationThree admin

            if startVersion >= 4 then
                MigrationUpgradeSupport.installMigrationFour admin

            if startVersion = 4 then
                seedDefiniteAttempt admin operationId

            let beforeBytes, beforeDigest = canonicalBytes admin operationId
            Expect.equal beforeDigest digest "Original canonical digest"

            let beforeCounts =
                if startVersion >= 3 then
                    Some(evidenceCounts admin operationId)
                else
                    None

            Migrations.apply admin
            let afterBytes, afterDigest = canonicalBytes admin operationId
            Expect.equal afterBytes beforeBytes "Canonical request bytes unchanged"
            Expect.equal afterDigest beforeDigest "Retained request digest unchanged"

            match beforeCounts with
            | Some expected ->
                Expect.equal
                    (evidenceCounts admin operationId)
                    expected
                    "Legacy marker and attempt/settlement rows unchanged"
            | None ->
                Expect.equal
                    (evidenceCounts admin operationId)
                    (1L, 0L, 0L)
                    "Migration 003 creates the pre-003 uncertainty marker"

            assertUpgraded app operationId (if startVersion = 4 then 1 else 0)))

let tests =
    testList
        "migration 005 recovery evidence"
        [
            upgradePath
                2
                "[CC-DB-001] migration 002 state upgrades to 005 with exact legacy evidence"
            upgradePath
                3
                "[CC-DB-001] migration 003 state upgrades to 005 with exact legacy evidence"
            upgradePath
                4
                "[CC-DB-001] migration 004 state upgrades to 005 without rewriting attempts"
        ]
