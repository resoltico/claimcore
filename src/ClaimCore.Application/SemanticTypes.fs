namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain

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
        DefaultPageSize: int
        MaximumPageSize: int
        RequestByteLimit: int
        CanonicalCommandFormat: int
        RequestFingerprintVersion: int
        LedgerEntryFormat: int
    }

type CoreDescription =
    {
        Contract: SemanticCoreContract
        SemanticFingerprint: SemanticCoreFingerprint
        Runtime: RuntimeContext
    }

type RuntimeOpenFault =
    | RuntimeConfigurationInvalid
    | RuntimeSchemaMismatch
    | RuntimeStoreUnavailable
    | RuntimeStoreIntegrityError
    | RuntimeCancelled

type LedgerPosition = { Sequence: int64; EntryHash: string }

[<RequireQualifiedAccess>]
type AcceptedAct =
    | Command of CommandKind
    | Stewardship of StewardshipKind

type OperationReceipt =
    {
        OperationId: Guid
        Position: LedgerPosition
        CaseReference: string
        Revision: int64
        Act: AcceptedAct
        Disposition: CaseDisposition option
        Snapshot: CaseView option
        RecordedAt: DateTimeOffset
        RecordedBy: Guid
    }

[<RequireQualifiedAccess>]
type CaseRecord =
    | Live of record: CaseView * disposition: CaseDisposition * availableCommands: CommandKind list
    | Erased of caseReference: string * revision: int64

[<RequireQualifiedAccess>]
type CaseSummary =
    | Live of
        caseReference: string *
        revision: int64 *
        status: CaseStatus *
        disposition: CaseDisposition
    | Erased of caseReference: string * revision: int64

type CaseListRequest =
    {
        AfterCursor: string option
        Limit: int
    }

type CaseSummaryPage =
    {
        Items: CaseSummary list
        NextCursor: string option
        AvailableCommands: CommandKind list
    }

type HistoryDetail =
    | Summary
    | Full

type HistoryRequest =
    {
        CaseReference: string
        AfterCursor: string option
        Limit: int
        Detail: HistoryDetail
    }

type ChangeSummary =
    {
        OperationId: Guid
        Position: LedgerPosition
        Revision: int64
        Act: AcceptedAct
        RecordedAt: DateTimeOffset
        RecordedBy: Guid
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
        IsAdvisory: bool
    }

[<RequireQualifiedAccess>]
type PreviewOutcome =
    | Reviewed of review: AdvisoryReview * requestSha256: string
    | AlreadyAccepted of OperationReceipt
    | Rejected of Rejection
    | Failed of CoreFault
    | Cancelled

[<RequireQualifiedAccess>]
type ExecuteOutcome =
    | Accepted of OperationReceipt
    | AlreadyAccepted of OperationReceipt
    | Rejected of Rejection
    | Failed of CoreFault
    | Cancelled
    | OutcomeUnknown of operationId: Guid * requestSha256: string

[<RequireQualifiedAccess>]
type AuthorityOutcome =
    | Applied of eventId: Guid * position: LedgerPosition * targetActorId: Guid
    | AlreadyApplied of eventId: Guid * position: LedgerPosition * targetActorId: Guid
    | Rejected of Rejection
    | Failed of CoreFault
    | Cancelled
    | OutcomeUnknown of eventId: Guid

type IAuthorityManagement =
    abstract RegisterActor:
        eventId: Guid * target: PrincipalKey * cancellationToken: CancellationToken ->
            Task<AuthorityOutcome>

    abstract SetGrant:
        eventId: Guid *
        target: PrincipalKey *
        role: Role *
        scope: GrantTarget *
        active: bool *
        cancellationToken: CancellationToken ->
            Task<AuthorityOutcome>

    abstract SetEnabled:
        eventId: Guid * target: PrincipalKey * enabled: bool * cancellationToken: CancellationToken ->
            Task<AuthorityOutcome>

    abstract Observe: eventId: Guid * cancellationToken: CancellationToken -> Task<AuthorityOutcome>

type IActorClaimsCore =
    abstract Definition: CancellationToken -> Task<QueryOutcome<CoreDescription>>
    abstract Preview: CommandRequest * CancellationToken -> Task<PreviewOutcome>
    abstract Execute: CommandRequest * CancellationToken -> Task<ExecuteOutcome>
    abstract Steward: StewardshipRequest * CancellationToken -> Task<ExecuteOutcome>

    abstract Get:
        caseReference: string * CancellationToken -> Task<QueryOutcome<Lookup<CaseRecord, string>>>

    abstract List: CaseListRequest * CancellationToken -> Task<QueryOutcome<CaseSummaryPage>>

    abstract History:
        HistoryRequest * CancellationToken -> Task<QueryOutcome<Lookup<HistoryResultPage, string>>>

    abstract ObserveOperation:
        operationId: Guid * CancellationToken -> Task<QueryOutcome<Lookup<OperationReceipt, Guid>>>

    abstract Authority: IAuthorityManagement
