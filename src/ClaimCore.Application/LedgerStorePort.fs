namespace ClaimCore.Application

open ClaimCore.Contract

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.RecordFormat

[<RequireQualifiedAccess>]
type internal StoreFailure =
    | Internal
    | Unavailable
    | SchemaMismatch
    | Integrity
    | Cancelled
    | CommitOutcomeUnknown

type internal LedgerHead =
    {
        Sequence: int64
        EntryHash: byte array
    }

type internal StoredCase =
    { CaseId: Guid; Snapshot: CaseSnapshot }

[<RequireQualifiedAccess>]
type internal StoredScope =
    | Case of caseId: Guid * revision: int64 * act: CaseActKind * businessDate: DateOnly
    | Authority of targetActorId: Guid * act: AuthorityKind

type internal StoredEntry =
    {
        Sequence: int64
        EntryHash: byte array
        OperationId: Guid
        ActorId: Guid
        RecordedAt: DateTimeOffset
        Scope: StoredScope
        PayloadCommitment: byte array
        PayloadNonce: byte array option
        Payload: byte array option
    }

type internal CaseReadView =
    {
        ObservedAt: DateTimeOffset
        Actor: AuthorityView
        ExistingOperation: StoredEntry option
        Case: StoredCase option
    }

type internal CaseLockedView =
    {
        Head: LedgerHead
        ObservedAt: DateTimeOffset
        PayloadNonce: byte array
        Actor: AuthorityView
        ExistingOperation: StoredEntry option
        Case: StoredCase option
    }

[<RequireQualifiedAccess>]
type internal CaseProjection =
    | Insert of caseId: Guid * snapshot: CaseSnapshot
    | Update of caseId: Guid * snapshot: CaseSnapshot
    | Erase of caseId: Guid * revision: int64

type internal EntryDraft =
    {
        Sequence: int64
        PreviousHash: byte array
        EntryHash: byte array
        OperationId: Guid
        ActorId: Guid
        RecordedAt: DateTimeOffset
        Scope: StoredScope
        PayloadCommitment: byte array
        PayloadNonce: byte array
        Payload: byte array
    }

[<RequireQualifiedAccess>]
type internal LedgerDecision<'projection> =
    | Append of EntryDraft * 'projection * OperationReceipt
    | Replay of OperationReceipt
    | Refuse of Rejection
    | Fail of CoreFault

[<RequireQualifiedAccess>]
type internal LedgerWriteResult =
    | Appended of OperationReceipt
    | Replayed of OperationReceipt
    | Refused of Rejection
    | Failed of CoreFault

type internal AuthorityLockedView =
    {
        Head: LedgerHead
        ObservedAt: DateTimeOffset
        PayloadNonce: byte array
        Actor: AuthorityView
        ExistingOperation: StoredEntry option
        Target: AuthorityView
        TargetCase: StoredCase option
        EnabledHumanOwners: int
        ActorCount: int
    }

[<RequireQualifiedAccess>]
type internal AuthorityProjection =
    | RegisterActor of actorId: Guid * principal: PrincipalKey
    | ProvisionOwner of actorId: Guid * principal: PrincipalKey
    | SetEnabled of actorId: Guid * enabled: bool
    | AddGrant of actorId: Guid * role: Role * caseId: Guid option
    | RemoveGrant of actorId: Guid * role: Role * caseId: Guid option

type internal ActorReadView =
    {
        ObservedAt: DateTimeOffset
        Actor: AuthorityView
    }

type internal CasePageSelection =
    {
        Visibility: CaseVisibility
        AfterReference: string option
    }

type internal HistoryReadView =
    {
        ObservedAt: DateTimeOffset
        Actor: AuthorityView
        Case: StoredCase option
    }

type internal StoredCasePage =
    {
        ObservedAt: DateTimeOffset
        Actor: AuthorityView
        Items: StoredCase list
        NextAfterReference: string option
    }

type internal StoredHistoryPage =
    {
        ObservedAt: DateTimeOffset
        Actor: AuthorityView
        Case: StoredCase option
        Entries: StoredEntry list
        NextAfterRevision: int64 option
    }

type internal StoredActorPage =
    {
        ObservedAt: DateTimeOffset
        Actor: AuthorityView
        Items: ActorSummary list
        NextAfterActorId: Guid option
    }

type internal GrantPosition = { Role: Role; CaseId: Guid option }

type internal GrantReadView =
    {
        ObservedAt: DateTimeOffset
        Actor: AuthorityView
        Target: ActorSummary option
    }

type internal StoredGrantPage =
    {
        ObservedAt: DateTimeOffset
        Actor: AuthorityView
        Target: ActorSummary
        Items: AuthorityGrant list
        NextAfterGrant: GrantPosition option
    }

type internal OperationReadView =
    {
        ObservedAt: DateTimeOffset
        Actor: AuthorityView
        Entry: StoredEntry option
        Case: StoredCase option
    }

/// Selection callbacks authorize and validate cursors within the snapshot that fetches the page.
/// Storage executes those selections and persists decisions; it invents no authority or transition rules.
type internal ILedgerStore =
    abstract WriteCase:
        principal: PrincipalKey *
        operationId: Guid *
        caseReference: string *
        decide: (CaseLockedView -> LedgerDecision<CaseProjection>) *
        cancellationToken: CancellationToken ->
            Task<Result<LedgerWriteResult, StoreFailure>>

    abstract WriteAuthority:
        principal: PrincipalKey *
        operationId: Guid *
        target: PrincipalKey *
        targetCaseReference: string option *
        decide: (AuthorityLockedView -> LedgerDecision<AuthorityProjection>) *
        cancellationToken: CancellationToken ->
            Task<Result<LedgerWriteResult, StoreFailure>>

    abstract ReadCase:
        principal: PrincipalKey *
        operationId: Guid option *
        caseReference: string *
        cancellationToken: CancellationToken ->
            Task<Result<CaseReadView, StoreFailure>>

    abstract ListCases:
        principal: PrincipalKey *
        readRoles: Role list *
        select: (ActorReadView -> Result<CasePageSelection, Rejection>) *
        limit: int *
        cancellationToken: CancellationToken ->
            Task<Result<Result<StoredCasePage, Rejection>, StoreFailure>>

    abstract ReadHistory:
        principal: PrincipalKey *
        caseReference: string *
        select: (HistoryReadView -> Result<int64, Rejection>) *
        limit: int *
        cancellationToken: CancellationToken ->
            Task<Result<Result<StoredHistoryPage, Rejection>, StoreFailure>>

    abstract ListActors:
        principal: PrincipalKey *
        select: (ActorReadView -> Result<Guid option, Rejection>) *
        limit: int *
        cancellationToken: CancellationToken ->
            Task<Result<Result<StoredActorPage, Rejection>, StoreFailure>>

    abstract ListGrants:
        principal: PrincipalKey *
        targetActorId: Guid *
        select: (GrantReadView -> Result<GrantPosition option, Rejection>) *
        limit: int *
        cancellationToken: CancellationToken ->
            Task<Result<Result<StoredGrantPage, Rejection>, StoreFailure>>

    abstract ReadOperation:
        principal: PrincipalKey * operationId: Guid * cancellationToken: CancellationToken ->
            Task<Result<OperationReadView, StoreFailure>>

    abstract ReadActor:
        principal: PrincipalKey * cancellationToken: CancellationToken ->
            Task<Result<ActorReadView, StoreFailure>>

type IBusinessCalendar =
    abstract TimeZoneId: string
    abstract DateAt: instant: DateTimeOffset -> DateOnly
