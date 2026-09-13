module ClaimCore.RecoveryQualificationTests.Suite

open Expecto

do afterRunTests ClaimCore.IntegrationTests.Fixtures.shutdown

[<Tests>]
let tests =
    testList
        "ClaimCore recovery qualification"
        [
            ClaimCore.IntegrationTests.RecoveryProcessTests.tests
            ClaimCore.IntegrationTests.RecoveryEvidenceTests.tests
            ClaimCore.IntegrationTests.RecoveryStateTests.tests
            ClaimCore.IntegrationTests.RecoveryCancellationTests.tests
        ]
    |> testSequenced
