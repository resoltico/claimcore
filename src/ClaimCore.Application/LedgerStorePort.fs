namespace ClaimCore.Application


open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.RecordFormat

[<RequireQualifiedAccess>]
type StoreFailure =
    | Internal
    | Unavailable
    | SchemaMismatch
    | Integrity
    | Cancelled
    | CommitOutcomeUnknown

type StoredCase =
    {
        CaseId: CaseId
        Snapshot: CaseSnapshot
        SnapshotBytes: byte array
    }

[<RequireQualifiedAccess>]
type EntryTarget =
    | Case of caseId: CaseId * revision: Revision * act: CaseActKind * businessDate: DateOnly
    | Authority of targetActorId: ActorId * act: AuthorityKind

type StoredEntry =
    {
        Sequence: Sequence
        EntryHash: byte array
        OperationId: OperationId
        ActorId: ActorId
        RecordedAt: DateTimeOffset
        Subject: EntryTarget
        PayloadCommitment: byte array
        PayloadNonce: byte array option
        Payload: byte array option
    }

[<RequireQualifiedAccess>]
type CaseProjection =
    | Insert of caseId: CaseId * snapshot: CaseSnapshot
    | Update of caseId: CaseId * snapshot: CaseSnapshot
    | Erase of caseId: CaseId * revision: Revision

type EntryDraft =
    {
        Sequence: Sequence
        PreviousHash: byte array
        EntryHash: byte array
        OperationId: OperationId
        ActorId: ActorId
        RecordedAt: DateTimeOffset
        Subject: EntryTarget
        PayloadCommitment: byte array
        PayloadNonce: byte array
        Payload: byte array
    }

[<RequireQualifiedAccess>]
type AuthorityProjection =
    | Register of actorId: ActorId * principal: PrincipalKey
    | ProvisionOwner of actorId: ActorId * principal: PrincipalKey
    | SetEnabled of actorId: ActorId * enabled: bool
    | AddGrant of actorId: ActorId * role: Role * caseId: CaseId option
    | RemoveGrant of actorId: ActorId * role: Role * caseId: CaseId option

type AuthorityQuery =
    {
        Principal: PrincipalKey
        CaseId: CaseId option
    }

type CasePageQuery =
    {
        Visibility: CaseVisibility
        ActorId: ActorId
        ReadRoles: Role list
        ForbiddenRoles: Role list
        AfterReference: string option
        Limit: int
    }

type HistoryPageQuery =
    {
        CaseId: CaseId
        AfterRevision: Revision
        Limit: int
    }

type ActorPageQuery =
    {
        AfterActorId: ActorId option
        Limit: int
    }

type GrantPageQuery =
    {
        ActorId: ActorId
        AfterGrant: GrantPosition option
        Limit: int
    }

type FenceKey =
    {
        ActorId: ActorId
        OperationId: OperationId
    }

/// A page carries only rows and a continuation position from the same transaction snapshot.
type Page<'Item, 'Position> =
    {
        Items: 'Item list
        Next: 'Position option
    }

type IReadTx =
    abstract ObservedAt: DateTimeOffset
    abstract Authority: AuthorityQuery -> Task<AuthorityView>
    abstract FindCase: CaseId -> Task<StoredCase option>
    abstract FindEntry: OperationId -> Task<StoredEntry option>

    abstract CasePage: CasePageQuery -> Task<Page<StoredCase, string>>

    abstract HistoryPage: HistoryPageQuery -> Task<Page<StoredEntry, Revision>>
    abstract ActorPage: ActorPageQuery -> Task<Page<Actor, ActorId>>

    abstract GrantPage: GrantPageQuery -> Task<Page<AuthoredGrant, GrantPosition>>

    abstract FindActor: ActorId -> Task<Actor option>
    abstract EnabledHumanOwners: unit -> Task<int>
    abstract ActorCount: unit -> Task<int>

type IWriteTx =
    inherit IReadTx
    abstract Head: LedgerPosition
    abstract Nonce: byte array
    abstract IsFenced: FenceKey -> Task<bool>
    abstract Append: EntryDraft -> Task<unit>
    abstract ProjectCase: CaseProjection -> Task<unit>
    abstract ProjectAuthority: AuthorityProjection -> Task<unit>
    abstract Fence: FenceKey -> Task<unit>

[<NoEquality; NoComparison>]
type ReadOperation<'result> =
    {
        Work: IReadTx -> Task<'result>
        CancellationToken: CancellationToken
    }

[<NoEquality; NoComparison>]
type WriteOperation<'result> =
    {
        Work: IWriteTx -> Task<Verdict<'result>>
        CancellationToken: CancellationToken
    }

type IStore =
    abstract Read: ReadOperation<'result> -> Task<Result<'result, StoreFailure>>
    abstract Write: WriteOperation<'result> -> Task<Result<Verdict<'result>, StoreFailure>>
