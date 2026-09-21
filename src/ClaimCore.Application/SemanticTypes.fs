namespace ClaimCore.Application

open System
open ClaimCore.Domain

/// A semantic contract digest is distinct from CLI and HTTP wire-contract fingerprints.
type SemanticCoreFingerprint = private SemanticCoreFingerprint of string

module SemanticCoreFingerprint =
    let create value = SemanticCoreFingerprint value
    let value (SemanticCoreFingerprint value) = value

type RuntimeContext =
    {
        ProductVersion: string
        EffectiveBusinessDate: DateOnly
        TimeZoneId: string
    }

type SemanticCoreContract =
    {
        Application: string
        Scope: string
        RuleSetVersion: int
        Fields: FieldDefinition list
        Commands: CommandDefinition list
        Statuses: CaseStatus list
        Rules: DomainRuleDefinition list
        RejectionDiagnostics: DiagnosticDefinition list
        FaultDiagnostics: DiagnosticDefinition list
        RecoveryDiagnostics: DiagnosticDefinition list
        DefaultPageSize: int
        MaximumPageSize: int
        RequestByteLimit: int
        CanonicalCommandFormat: int
        RequestFingerprintVersion: int
        RecoveryEnvelopeFormat: int
    }

type CoreDescription =
    {
        Contract: SemanticCoreContract
        SemanticFingerprint: SemanticCoreFingerprint
        Runtime: RuntimeContext
    }

/// Runtime opening is a composition concern rather than an HTTP/CLI outcome. Messages must remain
/// safe for local diagnostics and never include a connection string or provider exception detail.
type RuntimeOpenFault =
    | RuntimeConfigurationInvalid
    | RuntimeSchemaMismatch
    | RuntimeStoreUnavailable
    | RuntimeStoreIntegrityError
    | RuntimeCancelled

type CurrentCase =
    {
        Record: CaseView
        AvailableCommands: CommandKind list
    }

type OperationReceipt =
    {
        OperationId: Guid
        Snapshot: CaseView
        RecordedAt: DateTimeOffset
        RecordedBy: string
        Replayed: bool
        Command: CommandKind
    }

type CaseSummary =
    {
        CaseReference: string
        Revision: int64
        Status: CaseStatus
    }

type CaseListRequest =
    {
        AfterReference: string option
        Limit: int
    }

type CaseSummaryPage =
    {
        Items: CaseSummary list
        NextAfterReference: string option
    }

type HistoryDetail =
    | Summary
    | Full

type HistoryRequest =
    {
        CaseReference: string
        /// Opaque continuation token owned by Application. It is never a caller-supplied
        /// revision number, so adapters cannot turn a history page into an implicit query API.
        AfterCursor: string option
        Limit: int
        Detail: HistoryDetail
    }

type ChangeSummary =
    {
        OperationId: Guid
        Revision: int64
        Command: CommandKind
        RecordedAt: DateTimeOffset
        RecordedBy: string
    }

type HistoryEntry =
    | SummaryEntry of ChangeSummary
    | FullEntry of OperationReceipt

type HistoryResultPage =
    {
        Entries: HistoryEntry list
        NextCursor: string option
    }

type QueryOutcome<'value> =
    | Succeeded of 'value
    | Rejected of Rejection
    | Failed of CoreFault
    | Cancelled

type Lookup<'found, 'identity> =
    | Found of 'found
    | NotFound of 'identity

type DefiniteExecution =
    | Accepted of OperationReceipt
    | ExecutionRejected of operationId: Guid * rejection: Rejection
    | ExecutionRevokedBeforeExecution of operationId: Guid
    | FailedBeforeCommit of operationId: Guid * fault: CoreFault

type SettlementConfirmation =
    | Confirmed
    | Unconfirmed

type FieldDiff =
    {
        FieldName: string
        Before: string option
        After: string option
    }

type AdvisoryReview =
    {
        Before: CaseView option
        Proposed: CaseView
        Changes: FieldDiff list
        Context: RuntimeContext
        /// A review is a Domain-derived preview, never commit authority.
        IsAdvisory: bool
    }
