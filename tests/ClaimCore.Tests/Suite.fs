module ClaimCore.Tests.Suite

open Expecto

[<Tests>]
let tests =
    testList
        "ClaimCore deterministic suite"
        [
            PropertyHarnessTests.tests
            ScalarPropertyTests.tests
            RecordPropertyTests.tests
            TransitionPropertyTests.tests
            DomainTests.tests
            DomainValidationTests.tests
            AvailabilityTests.tests
            PaginationTests.tests
            CoreTests.tests
            TypedCoreTests.tests
            ExactReplayTests.tests
            ReceiptFirstTests.tests
            RecoveryOutcomeTests.tests
            RecoveryCancellationOutcomeTests.tests
            CoreQueryTests.tests
            CoreBoundaryParityTests.tests
            ExampleContractTests.tests
            ProtocolTests.tests
            CliExitContractTests.tests
            CanonicalRecordTests.tests
            DraftTests.tests
            BuildIdentityTests.tests
            ArchitectureTests.tests
            FieldSchemaTests.tests
            DomainDescriptorTests.tests
            ContractsTests.tests
            ConfigurationTests.tests
            CliProcessTests.tests
            PrivateFileSecurityTests.tests
            NativePrivateFileRaceTests.tests
            DatabasePrivateFileTests.tests
            CompilerBoundaryTests.tests
        ]
