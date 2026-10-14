namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain

[<RequireQualifiedAccess>]
type internal StoreFailure =
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

[<RequireQualifiedAccess>]
type internal StoredCaseState =
    | Live of CaseView * CaseDisposition
    | Erased

type internal StoredCase =
    {
        CaseId: Guid
        Reference: string
        Revision: int64
        State: StoredCaseState
    }

[<RequireQualifiedAccess>]
type internal StoredScope =
    | Case of caseId: Guid * revision: int64 * act: AcceptedAct * businessDate: DateOnly
    | Authority of targetActorId: Guid

type internal StoredEntry =
    {
        Sequence: int64
        EntryHash: byte array
        OperationId: Guid
        ActorId: Guid
        RecordedAt: DateTimeOffset
        Scope: StoredScope
        RequestSha256: byte array option
        Payload: byte array option
    }

type internal CaseReadView =
    {
        ObservedAt: DateTimeOffset
        Actor: ActorAuthority option
        ExistingOperation: StoredEntry option
        Case: StoredCase option
    }

type internal CaseLockedView =
    {
        Head: LedgerHead
        ObservedAt: DateTimeOffset
        PayloadNonce: byte array
        Actor: ActorAuthority option
        ExistingOperation: StoredEntry option
        Case: StoredCase option
    }

[<RequireQualifiedAccess>]
type internal CaseProjection =
    | Insert of caseId: Guid * snapshot: CaseView
    | Update of caseId: Guid * snapshot: CaseView * disposition: CaseDisposition
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
        RuleRevision: int option
        PayloadCommitment: byte array
        PayloadNonce: byte array
        Payload: byte array
        RequestSha256: byte array
    }

[<RequireQualifiedAccess>]
type internal CaseDecision =
    | Append of EntryDraft * CaseProjection
    | Replay of StoredEntry
    | Refuse of Rejection
    | Fail of CoreFault

[<RequireQualifiedAccess>]
type internal CaseWriteResult =
    | Appended of EntryDraft
    | Replayed of StoredEntry
    | Refused of Rejection
    | Failed of CoreFault

type internal AuthorityLockedView =
    {
        Head: LedgerHead
        ObservedAt: DateTimeOffset
        PayloadNonce: byte array
        Actor: ActorAuthority option
        ExistingOperation: StoredEntry option
        Target: ActorAuthority option
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

[<RequireQualifiedAccess>]
type internal AuthorityDecision =
    | Append of EntryDraft * AuthorityProjection
    | Replay of StoredEntry
    | Refuse of Rejection
    | Fail of CoreFault

[<RequireQualifiedAccess>]
type internal AuthorityWriteResult =
    | Appended of EntryDraft
    | Replayed of StoredEntry
    | Refused of Rejection
    | Failed of CoreFault

type internal ActorReadView =
    {
        ObservedAt: DateTimeOffset
        Actor: ActorAuthority option
    }

type internal StoredCasePage =
    {
        Actor: ActorAuthority option
        Items: StoredCase list
        NextAfterReference: string option
    }

type internal StoredHistoryPage =
    {
        Actor: ActorAuthority option
        Case: StoredCase option
        Entries: StoredEntry list
        NextAfterRevision: int64 option
    }

/// One locked load, one pure decision, one atomic append/projection/commit. Storage never decides policy.
type internal ILedgerStore =
    abstract WriteCase:
        principal: PrincipalKey *
        operationId: Guid *
        caseReference: string *
        decide: (CaseLockedView -> CaseDecision) *
        cancellationToken: CancellationToken ->
            Task<Result<CaseWriteResult, StoreFailure>>

    abstract WriteAuthority:
        principal: PrincipalKey *
        eventId: Guid *
        target: PrincipalKey *
        targetCaseReference: string option *
        decide: (AuthorityLockedView -> AuthorityDecision) *
        cancellationToken: CancellationToken ->
            Task<Result<AuthorityWriteResult, StoreFailure>>

    abstract ReadCase:
        principal: PrincipalKey *
        operationId: Guid option *
        caseReference: string *
        cancellationToken: CancellationToken ->
            Task<Result<CaseReadView, StoreFailure>>

    abstract ListCases:
        principal: PrincipalKey *
        visibility: (ActorAuthority -> CaseVisibility option) *
        afterReference: string option *
        limit: int *
        cancellationToken: CancellationToken ->
            Task<Result<StoredCasePage option, StoreFailure>>

    abstract ReadHistory:
        principal: PrincipalKey *
        caseReference: string *
        afterRevision: int64 *
        limit: int *
        cancellationToken: CancellationToken ->
            Task<Result<StoredHistoryPage, StoreFailure>>

    abstract ReadOperation:
        principal: PrincipalKey * operationId: Guid * cancellationToken: CancellationToken ->
            Task<
                Result<ActorAuthority option * StoredEntry option * StoredCase option, StoreFailure>
             >

    abstract ReadActor:
        principal: PrincipalKey * cancellationToken: CancellationToken ->
            Task<Result<ActorReadView, StoreFailure>>

type IBusinessCalendar =
    abstract TimeZoneId: string
    abstract DateAt: instant: DateTimeOffset -> DateOnly
