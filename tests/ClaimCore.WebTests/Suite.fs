module ClaimCore.WebTests.Suite

open ClaimCore.Contracts

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
            OidcStartupTests.tests
            PrincipalAdmissionTests.tests
            AuthTrustTests.tests
            AdmissionEdgeTests.tests
            AdmissionActorTests.tests
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
            AsyncTransportTests.tests
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
