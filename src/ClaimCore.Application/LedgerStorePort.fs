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
        Responsible: ActorId option
        FollowUp: CalendarDate option
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
            Responsible = fields |> Option.bind _.Work.Responsible
            FollowUp = fields |> Option.bind _.Work.FollowUp
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
        DateBasis: WorkDateBasis
        Visibility: CaseVisibility
        ActorId: ActorId
        ReadRoles: Role list
        ForbiddenRoles: Role list
        Filter: CaseListFilter<CaseSearchText>
    }

type CasePageQuery =
    {
        DateBasis: WorkDateBasis
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
            CaseSelection.DateBasis = this.DateBasis
            Visibility = this.Visibility
            ActorId = this.ActorId
            ReadRoles = this.ReadRoles
            ForbiddenRoles = this.ForbiddenRoles
            Filter = this.Filter
        }

type AssigneePageQuery =
    {
        CaseId: CaseId
        ReadRoles: Role list
        WorkRoles: Role list
        AfterActorId: ActorId option
        Limit: int
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

/// Point reads retain enough metadata to validate the control node without scanning history.
type RetainedControl =
    {
        Entry: StoredEntry
        PreviousHash: EntryHash
    }

/// A derived fence is evidence only when it agrees with its retained committed control.
type NonAcceptanceEvidence =
    {
        Control: RetainedControl option
        ProjectedAt: DateTimeOffset option
    }

/// A page carries only rows and a continuation position from the same transaction snapshot.
type Page<'Item, 'Position> =
    {
        Items: 'Item list
        Next: 'Position option
    }

/// The already-fetched LIMIT+1 row supplies comparison context without another read or a pinned traversal.
type CaseHistorySlice =
    {
        Page: Page<StoredEntry, Revision>
        Previous: StoredEntry option
    }

type IReadTx =
    abstract ObservedAt: DateTimeOffset
    abstract Maintenance: unit -> Task<RegisterMaintenance>
    abstract Authority: AuthorityQuery -> Task<AuthorityView>
    abstract FindCase: CaseId -> Task<StoredCase option>
    abstract FindCaseByReference: string -> Task<StoredCase option>
    abstract FindEntry: OperationId -> Task<StoredEntry option>
    abstract NonAcceptance: FenceKey -> Task<NonAcceptanceEvidence>

    abstract CasePage: CasePageQuery -> Task<Page<StoredCase, CaseListPosition>>
    abstract CaseSummary: CaseSelection -> Task<CaseWorklistSummary>

    abstract HistoryPage: HistoryPageQuery -> Task<CaseHistorySlice>
    abstract AuthorityHistoryPage: AuthorityHistoryPageQuery -> Task<Page<StoredEntry, Sequence>>
    abstract ActorPage: ActorPageQuery -> Task<Page<Actor, ActorId>>

    abstract GrantPage: GrantPageQuery -> Task<Page<ScopedGrant, GrantPosition>>

    abstract FindActor: ActorId -> Task<Actor option>
    abstract AssigneePage: AssigneePageQuery -> Task<Page<AccountSubject, ActorId>>
    abstract EnabledHumanOwners: unit -> Task<int>
    abstract ActorCount: unit -> Task<int>

type IWriteTx =
    inherit IReadTx
    abstract Head: LedgerPosition
    abstract Nonce: byte array
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
