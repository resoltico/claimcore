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

[<RequireQualifiedAccess>]
type View =
    | Cases
    | Authority
    | Operations

[<RequireQualifiedAccess>]
type InstallationOffer =
    | Open
    | AuthorityAct of AuthorityKind

[<RequireQualifiedAccess>]
type HistoryDetail =
    | Summary
    | Full

type CorrectionChoices =
    {
        Registration: CorrectionGroupAction list
        Decision: CorrectionGroupAction list
        Payment: CorrectionGroupAction list
    }

[<RequireQualifiedAccess>]
type CaseActOffer =
    | Act of act: CaseActKind * expectedRevision: int64
    | CorrectCase of expectedRevision: int64 * choices: CorrectionChoices

type CaseAdvice =
    {
        Acts: CaseActOffer list
        History: HistoryDetail list
    }

type ActorContext =
    {
        Runtime: RuntimeContext
        Capabilities: Capability list
        Views: View list
        Offers: InstallationOffer list
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
        Act: AuthorityKind
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
        Advice: CaseAdvice
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
        Offers: InstallationOffer list
        NextCursor: string option
    }

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
        FieldName: BusinessField
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
    | Reviewed of review: AdvisoryReview
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
    | OutcomeUnknown of operationId: Guid

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
