module ClaimCore.ConcurrencyQualificationTests.Suite

open Expecto

do afterRunTests ClaimCore.IntegrationTests.Fixtures.shutdown

[<Tests>]
let tests =
    testList
        "ClaimCore concurrency qualification"
        [
            ClaimCore.IntegrationTests.TransactionTests.concurrencyQualificationTests
            ClaimCore.IntegrationTests.RecoveryRaceTests.tests
        ]
    |> testSequenced
