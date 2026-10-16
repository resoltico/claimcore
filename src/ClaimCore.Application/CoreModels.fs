namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.RecordFormat

type RuntimeContext =
    {
        ProductVersion: string
        EffectiveBusinessDate: DateOnly
        TimeZoneId: string
    }

type InputChoiceOffer = { Input: string; Values: string list }

type OperationOffer =
    {
        Definition: string
        CaseReference: string option
        ExpectedRevision: int64 option
        Choices: InputChoiceOffer list
    }

type ActorContext =
    {
        Runtime: RuntimeContext
        Capabilities: Capability list
        Offers: OperationOffer list
    }

/// Admission vocabulary shared by persistence and the composition root.
[<RequireQualifiedAccess>]
type RuntimeOpenFault =
    | InvalidTransport
    | UnsupportedServer
    | UnsafeRole
    | SchemaMismatch
    | InstallationInvalid
    | HeadMismatch
    | Unavailable
    | Cancelled

type LedgerPosition = { Sequence: int64; EntryHash: string }

[<RequireQualifiedAccess>]
type SnapshotDisclosure =
    | Disclosed of CaseView
    | Withheld
    | Erased

type CaseReceipt =
    {
        Reference: string
        Revision: int64
        Act: CaseActKind
        Snapshot: SnapshotDisclosure
    }

type AuthorityReceipt =
    {
        TargetActorId: Guid
        Act: RecordedAuthorityKind
    }

[<RequireQualifiedAccess>]
type ReceiptScope =
    | Case of CaseReceipt
    | Authority of AuthorityReceipt

type OperationReceipt =
    {
        OperationId: Guid
        Position: LedgerPosition
        Scope: ReceiptScope
        RecordedAt: DateTimeOffset
        RecordedBy: Guid
    }

type CaseReadModel =
    {
        Snapshot: CaseSnapshot
        AvailableActs: CaseActKind list
        Offers: OperationOffer list
        HistoryDetails: string list
    }

[<RequireQualifiedAccess>]
type CaseSummary =
    | Live of
        caseReference: string *
        revision: int64 *
        status: CaseStatus *
        disposition: Disposition
    | Erased of caseReference: string * revision: int64

type CaseListRequest =
    {
        AfterCursor: string option
        Limit: int
    }

type CaseSummaryPage =
    {
        Items: CaseSummary list
        Offers: OperationOffer list
        NextCursor: string option
    }

[<RequireQualifiedAccess>]
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
        Act: CaseActKind
        RecordedAt: DateTimeOffset
        RecordedBy: Guid
    }

[<RequireQualifiedAccess>]
type HistoryEntry =
    | SummaryEntry of ChangeSummary
    | FullEntry of OperationReceipt

type HistoryResultPage =
    {
        Entries: HistoryEntry list
        NextCursor: string option
    }

type AuthorityListRequest =
    {
        AfterCursor: string option
        Limit: int
    }

type AuthorityGrant = { Role: Role; Scope: GrantTarget }

type ActorSummary =
    {
        ActorId: Guid
        Principal: PrincipalKey
        Enabled: bool
    }

type ActorReviewPage =
    {
        Items: ActorSummary list
        NextCursor: string option
    }

type AuthorityGrantListRequest =
    {
        ActorId: Guid
        AfterCursor: string option
        Limit: int
    }

type AuthorityGrantPage =
    {
        Actor: ActorSummary
        Items: AuthorityGrant list
        NextCursor: string option
    }

[<RequireQualifiedAccess>]
type AuthorityAct =
    | RegisterActor
    | SetGrant of role: Role * scope: GrantTarget * active: bool
    | SetEnabled of enabled: bool

type AuthorityRequest =
    {
        OperationId: Guid
        Target: PrincipalKey
        Act: AuthorityAct
    }

[<RequireQualifiedAccess>]
type QueryOutcome<'value> =
    | Succeeded of 'value
    | Rejected of Rejection
    | Failed of CoreFault
    | Cancelled

type FieldDiff =
    {
        FieldName: string
        Before: string option
        After: string option
    }

type AdvisoryReview =
    {
        Before: CaseSnapshot option
        Proposed: CaseSnapshot
        Changes: FieldDiff list
        Context: RuntimeContext
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

type IActorClaimsCore =
    abstract Context: CancellationToken -> Task<QueryOutcome<ActorContext>>
    abstract Preview: CaseRequest * CancellationToken -> Task<PreviewOutcome>
    abstract Execute: CaseRequest * CancellationToken -> Task<ExecuteOutcome>
    abstract Get: caseReference: string * CancellationToken -> Task<QueryOutcome<CaseReadModel>>
    abstract List: CaseListRequest * CancellationToken -> Task<QueryOutcome<CaseSummaryPage>>
    abstract History: HistoryRequest * CancellationToken -> Task<QueryOutcome<HistoryResultPage>>
    abstract Observe: operationId: Guid * CancellationToken -> Task<QueryOutcome<OperationReceipt>>
    abstract ExecuteAuthority: AuthorityRequest * CancellationToken -> Task<ExecuteOutcome>

    abstract ListActors:
        AuthorityListRequest * CancellationToken -> Task<QueryOutcome<ActorReviewPage>>

    abstract ListGrants:
        AuthorityGrantListRequest * CancellationToken -> Task<QueryOutcome<AuthorityGrantPage>>
