module ClaimCore.MigrationQualificationTests.Suite

open Expecto

do afterRunTests ClaimCore.IntegrationTests.Fixtures.shutdown

[<Tests>]
let tests =
    testList
        "ClaimCore migration qualification"
        [
            ClaimCore.IntegrationTests.MigrationUpgradeTests.tests
            ClaimCore.IntegrationTests.MigrationEvidenceUpgradeTests.tests
        ]
    |> testSequenced
