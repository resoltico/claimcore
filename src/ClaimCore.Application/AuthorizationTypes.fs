namespace ClaimCore.Application

open System

[<RequireQualifiedAccess>]
type Capability =
    | ReadDefinition
    | ListCases
    | ReadCase
    | ReadHistorySummary
    | ReadHistoryFull
    | ObserveOperation
    | EditCase
    | ListRecovery
    | InspectRecovery
    | ResolveRecovery
    | DismissRecovery
    | ExportRecovery
    | PreviewRecoveryImport
    | RetainRecoveryImport
    | ManageGrants
    | ManageHolds
    | ReviewLifecycle
    | VoidCase
    | ReinstateCase
    | RequestErasure
    | ApproveLifecycle
    | ApproveErasure
    | ReviewTombstone
    | ManageTombstoneHold
    | ApproveWitnessPrune
    | ApproveTerminalErasure
    | VerifyData
    | ReadBackupReport
    | ApproveRestore
    | ApproveCopySigner
    | ApproveCopyDeletion
    | ApproveCopyAdoption
    | ApproveWriterHandoff
    | ApproveRealDataActivation
    | ReviewRealDataActivation

/// Semantic core endpoints, not a CLI or HTTP route table. Adapters project this reviewed matrix.
[<RequireQualifiedAccess>]
type EndpointAction =
    | Definition
    | ListCases
    | GetCase
    | HistorySummary
    | HistoryFull
    | ObserveOperation
    | PrepareNewCase
    | ExecuteNewCase
    | PrepareCommand
    | ExecuteCommand
    | RecoveryList
    | RecoveryInspect
    | RecoveryResolve
    | RecoveryDismiss
    | RecoveryExport
    | RecoveryImportPreview
    | RecoveryImportRetain
    | ManageGrants
    | ManageHolds
    | ReviewLifecycle
    | VoidCase
    | ReinstateCase
    | RequestErasure
    | ApproveLifecycle
    | ApproveErasure
    | ReviewTombstone
    | ManageTombstoneHold
    | ApproveWitnessPrune
    | ApproveTerminalErasure
    | VerifyData
    | BackupReport
    | ApproveRestore
    | ApproveCopySigner
    | ApproveCopyDeletion
    | ApproveCopyAdoption
    | ApproveWriterHandoff
    | ApproveRealDataActivation
    | ReviewRealDataActivation

[<RequireQualifiedAccess>]
type ResourceScope =
    | Installation
    | Case of Guid
    | Operation of operationId: Guid * caseId: Guid

/// The same refusal covers a nonexistent identity and an inaccessible one.
[<RequireQualifiedAccess>]
type AuthorizationDecision =
    | Available of actorId: Guid * grantRevision: int64
    | Unavailable
