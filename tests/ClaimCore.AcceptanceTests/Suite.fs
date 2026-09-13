module ClaimCore.AcceptanceTests.Suite

open Expecto

do Tests.afterRunTests DatabaseFixture.shutdown

[<Tests>]
let tests = LifecycleTests.tests
