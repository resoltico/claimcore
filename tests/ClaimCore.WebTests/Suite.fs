module ClaimCore.WebTests.Suite

open ClaimCore.Contracts

open Expecto

let private configuration =
    testList
        "configuration"
        [
            ConfigurationTests.tests
            StartupLifetimeTests.tests
            OriginConfigurationTests.tests
            BindingAdmissionTests.tests
            WitnessConfigurationTests.tests
        ]
    |> testSequenced

let private boundaries =
    [
        OidcConfigurationTests.tests
        AuthFoundationTests.tests
        OidcStartupTests.tests
        OidcStartupCancellationTests.tests
        ProbeTransportTests.tests
        ProbeProcessTests.tests
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
        QueuedCapacityTests.tests
        TestServerSessionFailureTests.tests
        TestServerCoreOutcomeTests.tests
        TestServerRecoveryOutcomeTests.tests
        TestServerBoundaryTests.tests
    ]

[<Tests>]
let tests = testList "ClaimCore.Web" (configuration :: boundaries) |> testSequenced
