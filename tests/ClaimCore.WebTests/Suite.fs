module ClaimCore.WebTests.Suite

open Expecto

[<Tests>]
let tests =
    testList
        "ClaimCore.Web"
        [
            ConfigurationTests.tests
            AdmissionEdgeTests.tests
            HttpInputTests.tests
            ProgramEntryTests.tests
            WireTests.tests
            HostRouteTests.tests
            RouteTests.tests
            SecurityTests.tests
            WebHostSecurityTests.tests
            TestServerRouteTests.tests
            TestServerAdmissionTests.tests
            TestServerSessionFailureTests.tests
            TestServerCoreOutcomeTests.tests
            TestServerRecoveryOutcomeTests.tests
            TestServerBoundaryTests.tests
        ]
