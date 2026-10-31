namespace ClaimCore.Application


open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

[<RequireQualifiedAccess>]
type StoreFailure =
    | Internal
    | Unavailable
    | SchemaMismatch
    | Integrity
    | Cancelled
    | CommitOutcomeUnknown

type CaseIndex =
    {
        Status: CaseStatus option
        Progress: CaseProgress option
        ChangedAt: DateTimeOffset
        SearchText: string option
        NotifiedOn: CalendarDate option
        Claimed: Money option
        Payable: Money option
    }

module CaseIndex =
    let create changedAt (snapshot: CaseSnapshot) =
        let fields = CaseState.fields snapshot.State

        {
            Status = fields |> Option.map _.Status
            Progress = fields |> Option.map (fun fields -> CaseProgress.ofPayment fields.Progress)
            ChangedAt = changedAt
            SearchText = fields |> Option.map CaseSearch.text
            NotifiedOn =
                fields
                |> Option.map (fun fields ->
                    Registration.incidentNotificationDate fields.Registration)
            Claimed = fields |> Option.map (fun fields -> Registration.claimed fields.Registration)
            Payable =
                fields
                |> Option.bind (fun fields -> fields.Decision |> Option.map Decision.payable)
        }

type StoredCase =
    {
        CaseId: CaseId
        Snapshot: CaseSnapshot
        SnapshotBytes: byte array
        Index: CaseIndex
    }

[<RequireQualifiedAccess>]
type EntryTarget =
    | NonAcceptance of targetOperationId: OperationId
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
        CreationReference: CaseReference option
        PayloadCommitment: byte array
        PayloadNonce: byte array option
        Payload: byte array option
    }

[<RequireQualifiedAccess>]
type CaseProjection =
    | Insert of caseId: CaseId * snapshot: CaseSnapshot * index: CaseIndex
    | Update of caseId: CaseId * snapshot: CaseSnapshot * index: CaseIndex
    | Erase of caseId: CaseId * revision: Revision * changedAt: DateTimeOffset

type EntryDraft =
    {
        Sequence: Sequence
        PreviousHash: byte array
        EntryHash: byte array
        OperationId: OperationId
        ActorId: ActorId
        RecordedAt: DateTimeOffset
        Subject: EntryTarget
        CreationReference: CaseReference option
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

type CaseSelection =
    {
        Visibility: CaseVisibility
        ActorId: ActorId
        ReadRoles: Role list
        ForbiddenRoles: Role list
        Filter: CaseListFilter<CaseSearchText>
    }

type CasePageQuery =
    {
        Visibility: CaseVisibility
        ActorId: ActorId
        ReadRoles: Role list
        ForbiddenRoles: Role list
        Filter: CaseListFilter<CaseSearchText>
        AfterPosition: CaseListPosition option
        Limit: int
    }

    member this.Selection =
        {
            CaseSelection.Visibility = this.Visibility
            ActorId = this.ActorId
            ReadRoles = this.ReadRoles
            ForbiddenRoles = this.ForbiddenRoles
            Filter = this.Filter
        }

type HistoryPageQuery =
    {
        CaseId: CaseId
        AfterRevision: Revision
        Limit: int
    }

type AuthorityHistoryPageQuery =
    {
        TargetActorId: ActorId option
        BeforeSequence: Sequence option
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
    abstract FindCaseByReference: string -> Task<StoredCase option>
    abstract FindEntry: OperationId -> Task<StoredEntry option>

    abstract CasePage: CasePageQuery -> Task<Page<StoredCase, CaseListPosition>>
    abstract CaseSummary: CaseSelection -> Task<CaseWorklistSummary>

    abstract HistoryPage: HistoryPageQuery -> Task<Page<StoredEntry, Revision>>
    abstract AuthorityHistoryPage: AuthorityHistoryPageQuery -> Task<Page<StoredEntry, Sequence>>
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
