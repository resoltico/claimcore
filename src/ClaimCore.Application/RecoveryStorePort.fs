namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain

/// The Application-to-storage recovery seam is inaccessible to ordinary callers (and visible to
/// the composed PostgreSQL runtime through InternalsVisibleTo). `IRecoveryWorkflow` remains the
/// sole public recovery capability.
[<RequireQualifiedAccess>]
type internal PreparingContractKind =
    | CanonicalRecordV3
    | SemanticCoreV1

[<RequireQualifiedAccess>]
type internal PreparationLifecycle =
    | Unsubmitted
    | SubmissionStarted of DateTimeOffset
    | Dismissed of DateTimeOffset

[<NoEquality; NoComparison>]
type internal RecoveryPreparationDraft =
    {
        OperationId: Guid
        CanonicalRequestFormat: int
        RequestSha256: string
        CanonicalRequest: byte array
        PreparingApplicationVersion: string
        PreparingContractFingerprint: string
        PreparingContractKind: PreparingContractKind
    }

[<NoEquality; NoComparison>]
type internal RetainedPreparation =
    {
        OperationId: Guid
        CanonicalRequestFormat: int
        RequestSha256: string
        CanonicalRequest: byte array
        PreparedAt: DateTimeOffset
        PreparingApplicationVersion: string
        PreparingContractFingerprint: string
        PreparingContractKind: PreparingContractKind
        Lifecycle: PreparationLifecycle
    }

[<NoEquality; NoComparison>]
type internal RecoveryAttemptCursor =
    {
        OperationId: Guid
        StartedAt: DateTimeOffset
        AttemptId: Guid
    }

[<NoEquality; NoComparison>]
type internal RecoveryAttemptPage =
    {
        Items: PreparationAttempt list
        NextAfter: RecoveryAttemptCursor option

    }

/// Durable authority closure deliberately outlives optional preparation rows. It carries only the
/// exact identity material needed to refuse a conflicting or resurrected request.
[<NoEquality; NoComparison>]
type internal OperationRevocation =
    {
        OperationId: Guid
        CanonicalRequestFormat: int
        RequestSha256: string
        RevokedAt: DateTimeOffset
        Reason: string
    }

/// A header-only authority observation. It intentionally has no attempt collection and therefore
/// remains safe for admission, exact retry, import preview, and dismissal preflight paths.
[<RequireQualifiedAccess; NoEquality; NoComparison>]
type internal RecoveryStoredOperation =
    | Retained of RetainedPreparation * RecoveryAuthority
    | RevokedTombstone of OperationRevocation

/// Storage distinguishes a retained record from a revocation that intentionally outlived its
/// preparation. Keeping that distinction inside the port prevents adapters from inferring
/// authority from an obsolete lifecycle marker.
[<RequireQualifiedAccess; NoEquality; NoComparison>]
type internal RecoveryStoreInspection =
    | Retained of RetainedPreparation * RecoveryAttemptPage * RecoveryAuthority
    | RevokedTombstone of OperationRevocation

[<NoEquality; NoComparison>]
type internal RecoveryCursor =
    {
        View: RecoveryListView
        OccurredAt: DateTimeOffset
        OperationId: Guid
    }

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type internal RecoveryStoreListItem =
    | Retained of RetainedPreparation * RecoveryAuthority
    | Revoked of OperationRevocation

[<NoEquality; NoComparison>]
type internal RecoveryStorePage =
    {
        View: RecoveryListView
        Items: RecoveryStoreListItem list
        NextAfter: RecoveryCursor option
        PendingPreparationCount: int
        PendingCanonicalRequestBytes: int64
        MaximumPendingPreparations: int
        MaximumPendingCanonicalRequestBytes: int64
    }

[<RequireQualifiedAccess>]
[<NoEquality; NoComparison>]
type internal RecoveryRetain =
    | Created of RetainedPreparation
    | Existing of RetainedPreparation
    | ObservedAccepted of Receipt
    | Revoked of OperationRevocation

[<RequireQualifiedAccess>]
[<NoEquality; NoComparison>]
type internal RecoveryStart =
    | Started of attemptId: Guid * preparation: RetainedPreparation
    | AlreadyStarted of attemptId: Guid * preparation: RetainedPreparation
    | Dismissed of RetainedPreparation
    | ObservedAccepted of Receipt

[<RequireQualifiedAccess>]
type internal RecoverySettlement =
    | Accepted
    | Rejected
    | FailedBeforeCommit
    | RevokedBeforeExecution

/// One storage-owned transaction result for an admitted recovery attempt. The Domain callback is
/// still supplied by Application; storage merely serializes and persists its outcome.
[<RequireQualifiedAccess>]
[<NoEquality; NoComparison>]
type internal AdmittedExecution =
    | Accepted of Receipt
    | Rejected of DomainError * SettlementConfirmation
    | RevokedBeforeExecution of SettlementConfirmation
    | FailedBeforeCommit of CoreFailure * SettlementConfirmation
    | CommitOutcomeUnknown of operationId: Guid

[<RequireQualifiedAccess>]
[<NoEquality; NoComparison>]
type internal RecoveryDismissal =
    | Dismissed of RetainedPreparation
    | AlreadyDismissed of RetainedPreparation
    | SubmissionAlreadyStarted of RetainedPreparation
    | ObservedAccepted of Receipt
    | RevokedTombstone of OperationRevocation

[<RequireQualifiedAccess>]
type internal RecoveryStoreFailure =
    | InvalidInput of field: string
    | IdempotencyConflict
    | NotFound
    | CapacityExceeded
    | SchemaMismatch
    | StoreUnavailable
    | StoreCorrupt
    | ReadCancelled
    /// Caller cancellation was observed before the technical transaction's commit boundary.
    | CancelledBeforeCommit
    /// The storage adapter reached its technical mutation boundary but cannot prove the outcome.
    | TechnicalMutationUnknown

/// Storage implements this narrow technical port; adapters neither receive it nor access a store.
type internal IRecoveryStore =
    abstract InstallationLineage:
        cancellationToken: CancellationToken -> Task<Result<Guid, RecoveryStoreFailure>>

    abstract Retain:
        draft: RecoveryPreparationDraft * cancellationToken: CancellationToken ->
            Task<Result<RecoveryRetain, RecoveryStoreFailure>>

    abstract Get:
        operationId: Guid * cancellationToken: CancellationToken ->
            Task<Result<RecoveryStoredOperation option, RecoveryStoreFailure>>

    /// Detailed evidence is retrieved separately from the operational preparation header.
    abstract Inspect:
        operationId: Guid *
        after: RecoveryAttemptCursor option *
        limit: int *
        cancellationToken: CancellationToken ->
            Task<Result<RecoveryStoreInspection option, RecoveryStoreFailure>>

    abstract List:
        view: RecoveryListView *
        after: RecoveryCursor option *
        limit: int *
        cancellationToken: CancellationToken ->
            Task<Result<RecoveryStorePage, RecoveryStoreFailure>>

    abstract Start:
        operationId: Guid * cancellationToken: CancellationToken ->
            Task<Result<RecoveryStart, RecoveryStoreFailure>>

    abstract Settle:
        attemptId: Guid * outcome: RecoverySettlement * cancellationToken: CancellationToken ->
            Task<Result<unit, RecoveryStoreFailure>>

    /// Executes a previously admitted attempt under the same operation authority boundary used by
    /// start and dismissal. The storage implementation obtains business time after that lock.
    abstract ExecuteAdmitted:
        operation: PreparedOperation *
        attemptId: Guid *
        today: (unit -> DateOnly) *
        decide: (DateOnly -> Claim option -> Result<Claim, DomainError>) *
        cancellationToken: CancellationToken ->
            Task<Result<AdmittedExecution, RecoveryStoreFailure>>

    abstract Dismiss:
        operationId: Guid * requestSha256: string * cancellationToken: CancellationToken ->
            Task<Result<RecoveryDismissal, RecoveryStoreFailure>>
