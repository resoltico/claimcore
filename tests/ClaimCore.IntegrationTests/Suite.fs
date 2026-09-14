module ClaimCore.IntegrationTests.Suite

open Expecto

do afterRunTests Fixtures.shutdown

[<Tests>]
let tests =
    testList
        "ClaimCore PostgreSQL integration"
        [
            TransactionPaginationTests.tests
            TransactionRejectionTests.tests
            TransactionTests.tests
            SchemaTests.tests
            FieldStorageTests.tests
            CoreBoundaryTests.tests
            RuntimeLifecycleTests.tests
            StorageBoundaryTests.tests
            PreparationTests.tests
            AcceptedReplayStorageTests.tests
            PreparationBoundaryTests.tests
            MigrationUpgradeTests.tests
            MigrationEvidenceUpgradeTests.tests
            RecoveryProcessTests.tests
            RecoveryRaceTests.tests
            RecoveryEvidenceTests.tests
            RecoveryStateTests.tests
            RecoveryCancellationTests.tests
        ]
    |> testSequenced
