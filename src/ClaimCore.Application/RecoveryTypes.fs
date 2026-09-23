namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain

type RecoveryAction =
    | Resolve
    | Dismiss
    | Export

type PreparationState =
    | Unsubmitted
    | SubmissionStarted
    | Dismissed
    | Revoked

type RecoveryListView =
    | Pending
    | Terminal

type RecoveryAuthority =
    | PendingAuthority
    | AcceptedAuthority
    | RevokedAuthority

type PreparationSummary =
    {
        OperationId: Guid
        CaseReference: string
        Command: CommandKind
        PreparedAt: DateTimeOffset
        State: PreparationState
        Authority: RecoveryAuthority
        RequestSha256: string option
        AvailableActions: RecoveryAction list
    }

type PreparationAttempt =
    {
        AttemptId: Guid
        StartedAt: DateTimeOffset
        Settlement: string option
        SettledAt: DateTimeOffset option
    }

type PreparationAttemptPage =
    {
        Items: PreparationAttempt list
        NextCursor: string option

    }

type PreparationDetails =
    {
        Summary: PreparationSummary
        ExpectedVersion: int64
        AuthoredValues: (string * string) list
        CanonicalCommandFormat: int
        PreparingApplicationVersion: string
        PreparingContractFingerprint: string
        PreparingContractKind: string
        Attempts: PreparationAttemptPage
    }

/// A bounded recovery page deliberately excludes authored values and provenance.
type RevokedOperation =
    {
        OperationId: Guid
        RevokedAt: DateTimeOffset
        Reason: string
    }

/// A terminal recovery list deliberately distinguishes retained technical evidence from a compact
/// durable revocation whose preparation has been intentionally pruned. Tombstones never reveal
/// authored values, case references, or command content.
type RecoveryListItem =
    | RetainedRecoveryItem of PreparationSummary
    | RevokedRecoveryItem of RevokedOperation

/// The bounded recovery list defaults to Pending at every transport boundary. The terminal view is
/// explicit because it is an operator audit view, rather than work needing action.
type RecoveryPage =
    {
        View: RecoveryListView
        Items: RecoveryListItem list
        NextCursor: string option
        PendingPreparationCount: int
        PendingCanonicalRequestBytes: int64
        MaximumPendingPreparations: int
        MaximumPendingCanonicalRequestBytes: int64
        NearCapacity: bool
    }

/// Detailed recovery inspection is the only recovery read that exposes retained authored values.
/// The observation remains separate: a preparation can exist without an accepted receipt.
type RecoveryDetails =
    {
        Preparation: PreparationDetails
        Observation: Lookup<OperationReceipt, Guid>
    }

type RecoveryInspection =
    | RetainedInspection of RecoveryDetails
    | RevokedInspection of RevokedOperation

/// Recovery has its own refusal vocabulary. Reusing `QueryOutcome` would erase whether a refusal
/// came from business validation or from the preparation lifecycle.
type RecoveryQueryOutcome<'value> =
    | RecoverySucceeded of 'value
    | RecoveryRejected of RecoveryRejection
    | RecoveryFailed of CoreFault
    | RecoveryCancelled

type PrepareOutcome =
    | Prepared of details: PreparationDetails * review: AdvisoryReview
    | ObservedAccepted of OperationReceipt
    | RetainedForRecovery of details: PreparationDetails * reason: Rejection
    | PrepareRejected of operationId: Guid * rejection: Rejection
    | PrepareFailed of operationId: Guid * fault: CoreFault
    | CancelledBeforeAdmission of operationId: Guid
    | PreparationStateUnknown of operationId: Guid * requestSha256: string * fault: CoreFault

type SubmissionOutcome =
    | ObservedAccepted of OperationReceipt
    | Completed of
        preparation: PreparationSummary *
        attemptId: Guid *
        execution: DefiniteExecution *
        settlement: SettlementConfirmation
    | RejectedBeforeAttempt of preparation: PreparationSummary option * rejection: Rejection
    | FailedBeforeAttempt of preparation: PreparationSummary option * fault: CoreFault
    | PreparationStateUnknown of operationId: Guid * requestSha256: string * fault: CoreFault
    | CancelledBeforeAdmission of operationId: Guid
    | CancelledBeforeAttempt of PreparationSummary
    | AttemptAdmissionUnknown of preparation: PreparationSummary * fault: CoreFault
    | AttemptUnresolved of preparation: PreparationSummary * attemptId: Guid * fault: CoreFault

type ResolveOutcome =
    | ResolveObservedAccepted of OperationReceipt
    | ResolveCompleted of
        preparation: PreparationSummary *
        attemptId: Guid *
        execution: DefiniteExecution *
        settlement: SettlementConfirmation
    | RefusedBeforeAttempt of preparation: PreparationSummary option * rejection: RecoveryRejection
    | ResolveFailedBeforeAttempt of preparation: PreparationSummary option * fault: CoreFault
    | ResolveCancelledBeforeAdmission of operationId: Guid
    | ResolveCancelledBeforeAttempt of PreparationSummary
    | ResolveAttemptAdmissionUnknown of preparation: PreparationSummary * fault: CoreFault
    | ResolveAttemptUnresolved of
        preparation: PreparationSummary *
        attemptId: Guid *
        fault: CoreFault

type RecoveryDismissOutcome =
    | DismissedPreparation of PreparationDetails
    | AlreadyDismissedPreparation of PreparationDetails
    | AlreadyRevoked of RevokedOperation
    | DismissNotFound of Guid
    | DismissRefused of details: PreparationDetails option * rejection: RecoveryRejection
    | DismissFailed of CoreFault
    | DismissCancelledBeforeAdmission of Guid
    | DismissStateUnknown of operationId: Guid * requestSha256: string * fault: CoreFault

type RecoveryArtifactKind =
    | Envelope
    | UnboundCanonicalRecord

type RecoveryExport =
    {
        Bytes: byte array
        FileName: string
        MediaType: string
        RequestSha256: string
    }

type RecoveryImportEffect =
    {
        OperationId: Guid
        CaseReference: string
        Command: CommandKind
        ExpectedVersion: int64
        AuthoredValues: (string * string) list
        CanonicalCommandFormat: int
        RequestSha256: string
    }

type RecoveryImportPreview =
    {
        ArtifactKind: RecoveryArtifactKind
        SourceSha256: string
        DecodedEffect: RecoveryImportEffect
        ExistingPreparation: PreparationSummary option
    }

type RecoveryImportRetainOutcome =
    | RetainedPreparation of PreparationDetails
    | ExistingPreparation of PreparationDetails
    | ObservedAcceptedImport of OperationReceipt
    | ImportRejected of RecoveryRejection
    | ImportFailed of CoreFault
    | ImportCancelledBeforeAdmission
    | RetainStateUnknown of
        artifactKind: RecoveryArtifactKind *
        sourceSha256: string *
        operationId: Guid option *
        fault: CoreFault

type IRecoveryWorkflow =
    abstract List:
        view: RecoveryListView *
        afterCursor: string option *
        limit: int *
        cancellationToken: CancellationToken ->
            Task<RecoveryQueryOutcome<RecoveryPage>>

    abstract Inspect:
        operationId: Guid *
        afterCursor: string option *
        limit: int *
        cancellationToken: CancellationToken ->
            Task<RecoveryQueryOutcome<Lookup<RecoveryInspection, Guid>>>

    abstract Resolve:
        operationId: Guid * requestSha256: string * cancellationToken: CancellationToken ->
            Task<ResolveOutcome>

    abstract Dismiss:
        operationId: Guid *
        requestSha256: string *
        confirmed: bool *
        cancellationToken: CancellationToken ->
            Task<RecoveryDismissOutcome>

    abstract ExportEnvelope:
        operationId: Guid * requestSha256: string * cancellationToken: CancellationToken ->
            Task<RecoveryQueryOutcome<Lookup<RecoveryExport, Guid>>>

    abstract PreviewEnvelopeImport:
        source: byte array * cancellationToken: CancellationToken ->
            Task<RecoveryQueryOutcome<RecoveryImportPreview>>

    abstract RetainEnvelopeImport:
        source: byte array * sourceSha256: string * cancellationToken: CancellationToken ->
            Task<RecoveryImportRetainOutcome>

    abstract PreviewCanonicalRecordImport:
        source: byte array * cancellationToken: CancellationToken ->
            Task<RecoveryQueryOutcome<RecoveryImportPreview>>

    abstract RetainCanonicalRecordImport:
        source: byte array * sourceSha256: string * cancellationToken: CancellationToken ->
            Task<RecoveryImportRetainOutcome>
