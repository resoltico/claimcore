module ClaimCore.WebTests.Suite

open Expecto

[<Tests>]
let tests =
    testList
        "ClaimCore.Web"
        [
            testList
                "configuration"
                [
                    ConfigurationTests.tests
                    OriginConfigurationTests.tests
                    WitnessConfigurationTests.tests
                ]
            |> testSequenced
            OidcConfigurationTests.tests
            AuthFoundationTests.tests
            AdmissionEdgeTests.tests
            HttpInputTests.tests
            ManagementInputTests.tests
            LifecycleInputTests.tests
            TombstoneInputTests.tests
            TombstoneTerminalInputTests.tests
            SignerApprovalInputTests.tests
            CopyDeletionApprovalInputTests.tests
            CopyAdoptionApprovalInputTests.tests
            WriterHandoffApprovalInputTests.tests
            TransportDiagnosticTests.tests
            ProgramEntryTests.tests
            WireTests.tests
            RealDataActivationWireTests.tests
            RealDataActivationInputTests.tests
            HostRouteTests.tests
            RouteTests.tests
            SecurityTests.tests
            WebHostSecurityTests.tests
            TestServerTombstoneRouteTests.tests
            TestServerRouteTests.tests
            TestServerAdmissionTests.tests
            TestServerSessionFailureTests.tests
            TestServerCoreOutcomeTests.tests
            TestServerRecoveryOutcomeTests.tests
            TestServerBoundaryTests.tests
        ]
    |> testSequenced
