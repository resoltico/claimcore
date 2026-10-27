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

type LedgerHead =
    {
        Sequence: int64
        EntryHash: byte array
    }

type StoredCase =
    { CaseId: Guid; Snapshot: CaseSnapshot }

[<RequireQualifiedAccess>]
type EntryTarget =
    | Case of caseId: Guid * revision: int64 * act: CaseActKind * businessDate: DateOnly
    | Authority of targetActorId: Guid * act: AuthorityKind

type StoredEntry =
    {
        Sequence: int64
        EntryHash: byte array
        OperationId: Guid
        ActorId: Guid
        RecordedAt: DateTimeOffset
        Subject: EntryTarget
        PayloadCommitment: byte array
        PayloadNonce: byte array option
        Payload: byte array option
    }

[<RequireQualifiedAccess>]
type CaseProjection =
    | Insert of caseId: Guid * snapshot: CaseSnapshot
    | Update of caseId: Guid * snapshot: CaseSnapshot
    | Erase of caseId: Guid * revision: int64

type EntryDraft =
    {
        Sequence: int64
        PreviousHash: byte array
        EntryHash: byte array
        OperationId: Guid
        ActorId: Guid
        RecordedAt: DateTimeOffset
        Subject: EntryTarget
        PayloadCommitment: byte array
        PayloadNonce: byte array
        Payload: byte array
    }

[<RequireQualifiedAccess>]
type AuthorityProjection =
    | Register of actorId: Guid * principal: PrincipalKey
    | ProvisionOwner of actorId: Guid * principal: PrincipalKey
    | SetEnabled of actorId: Guid * enabled: bool
    | AddGrant of actorId: Guid * role: Role * caseId: Guid option
    | RemoveGrant of actorId: Guid * role: Role * caseId: Guid option

type CasePageSelection =
    {
        Visibility: CaseVisibility
        AfterReference: string option
    }

/// A page carries only rows and a continuation position from the same transaction snapshot.
type Page<'Item, 'Position> =
    {
        Items: 'Item list
        Next: 'Position option
    }

type IReadTx =
    abstract ObservedAt: DateTimeOffset
    abstract Authority: PrincipalKey * Guid option -> Task<AuthorityView>
    abstract FindCase: Guid -> Task<StoredCase option>
    abstract FindEntry: Guid -> Task<StoredEntry option>

    abstract CasePage:
        CaseVisibility * Guid * Role list * Role list * string option * int ->
            Task<Page<StoredCase, string>>

    abstract HistoryPage: Guid * int64 * int -> Task<Page<StoredEntry, int64>>
    abstract ActorPage: Guid option * int -> Task<Page<Actor, Guid>>

    abstract GrantPage:
        Guid * GrantPosition option * int -> Task<Page<AuthorityGrant, GrantPosition>>

    abstract FindActor: Guid -> Task<Actor option>
    abstract EnabledHumanOwners: unit -> Task<int>
    abstract ActorCount: unit -> Task<int>

type IWriteTx =
    inherit IReadTx
    abstract Head: LedgerHead
    abstract Nonce: byte array
    abstract IsFenced: Guid * Guid -> Task<bool>
    abstract Append: EntryDraft -> Task<unit>
    abstract ProjectCase: CaseProjection -> Task<unit>
    abstract ProjectAuthority: AuthorityProjection -> Task<unit>
    abstract Fence: Guid * Guid -> Task<unit>

type IStore =
    abstract Read: (IReadTx -> Task<'T>) * CancellationToken -> Task<Result<'T, StoreFailure>>

    abstract Write:
        (IWriteTx -> Task<Verdict<'T>>) * CancellationToken ->
            Task<Result<Verdict<'T>, StoreFailure>>
