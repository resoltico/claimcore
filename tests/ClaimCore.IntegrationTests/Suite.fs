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
            FreshBaselineTests.tests
            BaselineRefusalTests.tests
            RecoveryProcessTests.tests
            RecoveryRaceTests.tests
            RecoveryEvidenceTests.tests
            RecoveryLifecycleAuthorityTests.tests
            TerminalCapacityTests.tests
            RecoveryStateTests.tests
            RecoveryCancellationTests.tests
            AdministrationCompletionTests.tests
        ]
    |> testSequenced
