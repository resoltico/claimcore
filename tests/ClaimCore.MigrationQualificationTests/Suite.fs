module ClaimCore.MigrationQualificationTests.Suite

open Expecto

do afterRunTests ClaimCore.IntegrationTests.Fixtures.shutdown

[<Tests>]
let tests =
    testList
        "ClaimCore fresh baseline qualification"
        [
            ClaimCore.IntegrationTests.FreshBaselineTests.tests
            ClaimCore.IntegrationTests.BaselineRefusalTests.tests
        ]
    |> testSequenced
