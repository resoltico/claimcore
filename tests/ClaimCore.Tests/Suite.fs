module ClaimCore.Tests.Suite

open Expecto

let private foundation =
    [
        PropertyHarnessTests.tests
        ScalarPropertyTests.tests
        RecordPropertyTests.tests
        CommandIdentityIsolationTests.tests
        TransitionPropertyTests.tests
        DomainTests.tests
        CaseDispositionTests.tests
        CaseErasureTests.tests
        CaseLifecycleProjectionTests.tests
        CasePurgeAuthorizationTests.tests
        CaseCorrectionTests.tests
        BusinessDateTests.tests
        DomainAdmissionTests.tests
        CorrectionMatrixTests.tests
        DomainValidationTests.tests
        AvailabilityTests.tests
        PaginationTests.tests
        CoreTests.tests
        TypedCoreTests.tests
        ExactReplayTests.tests
        PreparationAttributionTests.tests
        SignedRecoveryImportTests.tests
        ExportDeliveryTests.tests
        ReceiptFirstTests.tests
        RecoveryOutcomeTests.tests
        RecoveryCancellationOutcomeTests.tests
        RecoveryAuthorityTests.tests
        CoreQueryTests.tests
        CaseListCursorTests.tests
        CoreBoundaryParityTests.tests
        ExampleContractTests.tests
    ]

let private surfaces =
    [
        ProtocolTests.tests
        CliExitContractTests.tests
        CanonicalRecordTests.tests
        FreshRecoveryFormatTests.tests
        RecoveryArtifactV3Tests.tests
        DraftTests.tests
        BuildIdentityTests.tests
        ArchitectureTests.tests
        InstallationCalendarTests.tests
        FieldSchemaTests.tests
        DomainDescriptorTests.tests
        ContractsTests.tests
        EndpointExtensionTests.tests
        SemanticIdentityTests.tests
        RejectionDiagnosticTests.tests
        RejectionEmissionTests.tests
        OutcomeDiagnosticTests.tests
        OutcomeEmissionTests.tests
        ConfigurationTests.tests
        CliProcessTests.tests
        BoundedProcessTests.tests
        RemoteClientTests.tests
        HttpResponseDeadlineTests.tests
        RemoteResponseBoundsTests.tests
        IssuerIdentityTests.tests
        RemoteInvocationTests.tests
        ActorAuthorizationTests.tests
        ActorAuthorizationMatrixTests.tests
        ActorCommandCapabilityTests.tests
        ActorPrincipalIdentityTests.tests
        PrivateFileSecurityTests.tests
        PrivateFileHashTests.tests
        NativePrivateFileRaceTests.tests
        DatabasePrivateFileTests.tests
        CompilerBoundaryTests.tests
        FrameDeliveryTests.tests
        ProtocolDiagnosticTests.tests
        AdministrationDiagnosticTests.tests
    ]

[<Tests>]
let tests = testList "ClaimCore deterministic suite" (foundation @ surfaces)
