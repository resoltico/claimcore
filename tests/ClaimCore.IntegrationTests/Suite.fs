module ClaimCore.IntegrationTests.Suite

open Expecto

do afterRunTests Fixtures.shutdown

let private coreAndRecovery =
    [
        TransactionPaginationTests.tests
        CaseListCursorProtectionTests.tests
        TransactionRejectionTests.tests
        TransactionTests.tests
        SchemaTests.tests
        FieldStorageTests.tests
        CoreBoundaryTests.tests
        RuntimeLifecycleTests.tests
        RuntimeAuditCadenceTests.tests
        AuthorityOperationFenceTests.tests
        StorageBoundaryTests.tests
        PreparationTests.tests
        AcceptedReplayStorageTests.tests
        WitnessProtocolTests.tests
        WitnessKeyCustodyTests.tests
        RecoveryArtifactKeyCustodyTests.tests
        RecoveryArtifactExportTests.tests
        RecoveryArtifactImportTests.tests
        RecoveryArtifactImportPreviewTests.tests
        TechnicalWitnessTests.tests
        PreparationBoundaryTests.tests
        FreshBaselineTests.tests
        BaselineRefusalTests.tests
        RecoveryProcessTests.tests
        RecoveryRaceTests.tests
        RecoveryEvidenceTests.tests
        RecoveryLifecycleAuthorityTests.tests
        TerminalCapacityTests.tests
        RecoveryStateTests.tests
        RecoveryCancellationTests.tests
        AdministrationCompletionTests.tests
        DataAuditTests.tests
    ]

let private restoreAndBackup =
    [
        DatabaseVerifyDataTests.tests
        RestoreReportOwnerRosterTests.tests
        RestoreReportPairBindingTests.tests
        RestorePublicationTests.tests
        RestoreProduceCanonicalTests.tests
        RestoreProduceOutputTests.tests
        RestoreProduceArchiveTests.tests
        RestoreProduceBarrierTests.tests
        RestoreProduceSignerTests.tests
        RestoreProduceWalCoverageTests.tests
        RestoreFencedTailTests.tests
        RestoreFencedTailInputTests.tests
        BackupHealthSourceTests.tests
        BackupHealthReconciliationTests.tests
        RestoreProduceAuditTests.tests
        RestoreProducePhysicalTests.tests
        RestoreProduceAdvancedPairTests.tests
        RestoreProduceSignedPairTests.tests
        RestoreWriterHandoffPhysicalTests.tests
        RestoreFencedTailPhysicalTests.tests
        WriterActivationTests.tests
        WriterActivationCrashTests.tests
        IndependentHostProbeTests.tests
        IndependentHostTopologyTests.tests
        ActorGrantStoreTests.tests
        WitnessAuditorTests.tests
        ActorGrantLookupTests.tests
        ActorBoundCoreTests.tests
        BackupCaptureFenceTests.tests
        BackupCaptureFrameTests.tests
    ]

let private actorAndCustody =
    [
        BackupCaptureClaimsTests.tests
        BackupCapturePathsTests.tests
        BackupCaptureEvidenceTests.tests
        BackupCaptureSessionTests.tests
        BackupCapturePhysicalTests.tests
        CaseListAuthorityTests.tests
        ActorBoundReplayTests.tests
        ActorBoundRecoveryTests.tests
        RecoveryListAuthorityTests.tests
        ManagedCopyEventHashTests.tests
        ManagedCopySignerTests.tests
        ManagedCopySignerPurposeTests.tests
        InstallationLossRetirementTests.tests
        InstallationLossRetirementCrashTests.tests
        InstallationLossRetirementStalePairTests.tests
        InstallationLossRetirementProcessTests.tests
        ManagedCopyIngestTests.tests
        ManagedCopyTransitionRejectionTests.tests
        WriterHandoffApprovalTests.tests
        RealDataActivationApprovalTests.tests
        RealDataActivationApprovalUncertaintyTests.tests
        RealDataActivationMechanicsTests.tests
        WriterHandoffRevocationTests.tests
        WriterHandoffHolderTests.tests
        WriterHandoffProtocolTests.tests
        WriterHandoffAbortProtocolTests.tests
        ManagedCopyInventoryTests.tests
        ManagedCopyVerifiedDeletionTests.tests
        ManagedCopyVerifiedRestoreTests.tests
        ManagedCopyPhysicalProcessTests.tests
        BackupHealthRuntimeActorRaceTests.tests
        ManagedCopyHoldTests.tests
        ManagedCopyKindTests.tests
        ManagedCopyCryptoTests.tests
    ]

let private lifecycleAndPrivacy =
    [
        ManagedCopyWitnessTipTests.tests
        ActorManagementTests.tests
        CaseLifecycleStoreTests.tests
        CaseListBlockedTimingTests.tests
        CaseErasureFenceTests.tests
        CaseLifecycleAuditTests.tests
        CaseLifecycleReconcileTests.tests
        CaseLifecycleCapacityTests.tests
        CaseErasurePendingTests.tests
        CaseErasureFencesTests.tests
        CaseErasurePurgeTests.tests
        CaseTombstoneAuthorityTests.tests
        CaseWitnessPayloadPruneTests.tests
        CaseWitnessPayloadPruneRetryTests.tests
        CaseWitnessPayloadPruneAuditTests.tests
        CaseWitnessPruneFunctionTests.tests
        CaseTombstoneTerminalApprovalTests.tests
        CaseTombstoneTerminalUncertaintyTests.tests
        CaseTombstoneTerminalAuditTamperTests.tests
        CaseTombstoneTerminalOwnerTests.tests
        DatabaseTerminalCopyAbsenceTests.tests
        DatabaseTerminalCopyAbsenceProcessTests.tests
        DatabaseWitnessPruneProcessTests.tests
        CaseErasurePurgeAuditTests.tests
        CaseErasureArtifactTests.tests
        ProductExportEventChainTests.tests
        ManagedCopyAdoptionApprovalTests.tests
        ManagedCopyAdoptionOwnerTests.tests
        ManagedCopyAdoptedProductTests.tests
        ManagedCopyAdoptedTransitionTests.tests
        ManagedCopyExternalPublicationTests.tests
        ManagedCopyExternalPublicationPruneTests.tests
        ManagedCopyExternalPublicationProcessTests.tests
        ManagedCopyAdoptionProcessTests.tests
        CaseWitnessPayloadPruneExportTests.tests
        CaseErasurePurgeAdmissionTests.tests
    ]

let private groups =
    coreAndRecovery @ restoreAndBackup @ actorAndCustody @ lifecycleAndPrivacy

[<Tests>]
let tests = testList "ClaimCore PostgreSQL integration" groups |> testSequenced
