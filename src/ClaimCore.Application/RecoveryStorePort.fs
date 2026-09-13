namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks

/// The Application-to-storage recovery seam is inaccessible to ordinary callers (and visible to
/// the composed PostgreSQL runtime through InternalsVisibleTo). `IRecoveryWorkflow` remains the
/// sole public recovery capability.
[<RequireQualifiedAccess>]
type internal PreparingContractKind =
    | LegacyUnclassified
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
        Attempts: PreparationAttempt list
        LegacyUncertainty: bool
    }

[<NoEquality; NoComparison>]
type internal RecoveryCursor =
    {
        PreparedAt: DateTimeOffset
        OperationId: Guid
    }

[<NoEquality; NoComparison>]
type internal RecoveryStorePage =
    {
        Items: RetainedPreparation list
        NextAfter: RecoveryCursor option
    }

[<RequireQualifiedAccess>]
[<NoEquality; NoComparison>]
type internal RecoveryRetain =
    | Created of RetainedPreparation
    | Existing of RetainedPreparation

[<RequireQualifiedAccess>]
[<NoEquality; NoComparison>]
type internal RecoveryStart =
    | Started of attemptId: Guid * preparation: RetainedPreparation
    | AlreadyStarted of attemptId: Guid * preparation: RetainedPreparation
    | Dismissed of RetainedPreparation

[<RequireQualifiedAccess>]
type internal RecoverySettlement =
    | Accepted
    | Rejected
    | FailedBeforeCommit

[<RequireQualifiedAccess>]
[<NoEquality; NoComparison>]
type internal RecoveryDismissal =
    | Dismissed of RetainedPreparation
    | AlreadyDismissed of RetainedPreparation
    | SubmissionAlreadyStarted of RetainedPreparation

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
            Task<Result<RetainedPreparation option, RecoveryStoreFailure>>

    abstract List:
        after: RecoveryCursor option * limit: int * cancellationToken: CancellationToken ->
            Task<Result<RecoveryStorePage, RecoveryStoreFailure>>

    abstract Start:
        operationId: Guid * cancellationToken: CancellationToken ->
            Task<Result<RecoveryStart, RecoveryStoreFailure>>

    abstract Settle:
        attemptId: Guid * outcome: RecoverySettlement * cancellationToken: CancellationToken ->
            Task<Result<unit, RecoveryStoreFailure>>

    abstract Dismiss:
        operationId: Guid * cancellationToken: CancellationToken ->
            Task<Result<RecoveryDismissal, RecoveryStoreFailure>>
