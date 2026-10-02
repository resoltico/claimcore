module ClaimCore.IntegrationTests.Suite

open System
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
        RuntimeResourceCleanupTests.tests
        RuntimeAuditCadenceTests.tests
        RuntimeSchedulingTests.tests
        AuthorityOperationFenceTests.tests
        WitnessedCapacityTests.tests
        StorageBoundaryTests.tests
        DomainEvidenceTests.tests
        PreparationTests.tests
        PreparationHoldRetentionTests.tests
        MutationDisclosureTests.tests
        AcceptedReplayStorageTests.tests
        AcceptedHistoryRetentionTests.tests
        AcceptedReplayWitnessTests.tests
        AuthorityOperationFenceCloseTests.tests
        RevocationIntentIdentityTests.tests
        SettledAttemptExecutionTests.tests
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
        RecoverySnapshotTests.tests
        RecoveryLifecycleAuthorityTests.tests
        RecoveryStateTests.tests
        RecoveryCancellationTests.tests
        AdministrationCompletionTests.tests
        DataAuditTests.tests
    ]

/// One test that drives 1,024 accepted operations in sequence; alone, it bounds one partition.
let private terminalCapacity = [ TerminalCapacityTests.tests ]

/// Restored-writer activation runs real restores and is the longest run of physical-copy tests.
let private restoredWriterActivation =
    [ WriterActivationTests.tests; WriterActivationCrashTests.tests ]

let private restoreAndBackup =
    [
        DatabaseVerifyDataTests.tests
        RestoreReportOwnerRosterTests.tests
        RestoreReportPairBindingTests.tests
        RestorePublicationTests.tests
        RestoreProduceCanonicalTests.tests
        RestoreReportScopeTests.tests
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
        CaseListCapacityTests.tests
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

/// The registered partitions. Each is run by its own process against its own primary and witness
/// clusters, so partitions may run concurrently; running every partition in one process (no
/// selection) discovers and executes exactly the union. Configuration registers the ids; generated
/// inventories under tests/inventory own the expected tests, without duplicated counts.
let private partitions =
    [
        "terminal-capacity", terminalCapacity
        "core-and-recovery", coreAndRecovery
        "restored-writer-activation", restoredWriterActivation
        "restore-and-backup", restoreAndBackup
        "actor-and-custody", actorAndCustody
        "lifecycle-and-privacy", lifecycleAndPrivacy
    ]

let private selectedGroups () =
    match Environment.GetEnvironmentVariable "CLAIMCORE_INTEGRATION_PARTITION" with
    | null
    | "" -> partitions |> List.collect snd
    | id ->
        match partitions |> List.tryFind (fun (name, _) -> name = id) with
        | Some(_, group) -> group
        | None -> invalidOp "CLAIMCORE_INTEGRATION_PARTITION names no registered partition."

[<Tests>]
let tests =
    testList "ClaimCore PostgreSQL integration" (selectedGroups ()) |> testSequenced
