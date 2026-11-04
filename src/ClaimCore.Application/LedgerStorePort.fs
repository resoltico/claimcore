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
        WorkSearchText: string option
        NotifiedOn: CalendarDate option
        Claimed: Money option
        Payable: Money option
    }

module CaseIndex =
    let create changedAt (snapshot: CaseSnapshot) =
        let fields = CaseState.fields snapshot.State

        {
            Responsible = None
            FollowUp = None
            Status = fields |> Option.map _.Status
            Progress = fields |> Option.map (fun fields -> CaseProgress.ofPayment fields.Progress)
            ChangedAt = changedAt
            SearchText = fields |> Option.map CaseSearch.text
            WorkSearchText = None
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
    | Work of WorkEntryMetadata
    | Reminder of ReminderEntryMetadata

type StoredEntry =
    {
        Effects: LedgerEffects
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

/// One accepted operation, including every contiguous physical effect in sequence order.
[<NoComparison>]
type StoredOperation =
    private
    | StoredOperation of root: StoredEntry * entries: StoredEntry list

    member this.Root = let (StoredOperation(root, _)) = this in root
    member this.Entries = let (StoredOperation(_, entries)) = this in entries
    member this.OperationId = this.Root.OperationId
    member this.ActorId = this.Root.ActorId
    member this.Subject = this.Root.Subject

    member this.Position =
        let last = List.last this.Entries
        LedgerPosition.create last.Sequence (EntryHash.ofBytes last.EntryHash |> Option.get)

module StoredOperation =
    /// Metadata completeness is independent of payload availability after authorized erasure.
    let create (entries: StoredEntry list) =
        let failure () = Error CoreFault.StoreIntegrityError

        match entries with
        | [] -> failure ()
        | root :: _ ->
            let count = LedgerEffects.count root.Effects
            let authority = LedgerEffects.authorityOperation root.Effects

            let target =
                match root.Subject with
                | EntryTarget.Authority(id, _) -> Some id
                | _ -> None

            let coherent index (entry: StoredEntry) =
                LedgerEffects.index entry.Effects = index
                && LedgerEffects.count entry.Effects = count
                && LedgerEffects.authorityOperation entry.Effects = authority
                && entry.OperationId = root.OperationId
                && entry.ActorId = root.ActorId
                && entry.RecordedAt = root.RecordedAt
                && Sequence.value entry.Sequence = Sequence.value root.Sequence + int64 index
                && not (obj.ReferenceEquals(entry.EntryHash, null))
                && entry.EntryHash.Length = 32
                && (match entry.Subject with
                    | EntryTarget.Authority(_, act) ->
                        LedgerEffects.describesAuthority act entry.Effects
                    | _ -> entry.Effects = LedgerEffects.single)
                && (match target, entry.Subject with
                    | Some expected, EntryTarget.Authority(actual, _) -> expected = actual
                    | None, _ -> count = 1
                    | _ -> false)

            let nonces = entries |> List.choose _.PayloadNonce

            let accepted =
                match root.Subject with
                | EntryTarget.NonAcceptance _ -> false
                | _ -> true

            if
                accepted
                && root.Sequence >= Sequence.first
                && count >= 1
                && count <= LedgerEffects.MaximumCount
                && entries.Length = count
                && (List.mapi coherent entries |> List.forall id)
                && (List.distinct nonces |> List.length) = nonces.Length
                && (count = 1 || target.IsSome)
            then
                Ok(StoredOperation(root, entries))
            else
                failure ()

type StoredWork =
    {
        CaseId: CaseId
        Revision: Revision
        Snapshot: CaseWorkSnapshot option
        SnapshotBytes: byte array
        ChangedAt: DateTimeOffset option
        RecordedBy: ActorId option
    }

type StoredReminder =
    {
        ReminderId: ReminderId
        CaseId: CaseId
        Revision: Revision
        Snapshot: Reminder option
        SnapshotBytes: byte array
        RecordedAt: DateTimeOffset
        RecordedBy: ActorId
    }

type CaseWorkSummary =
    {
        Work: StoredWork
        Next: Reminder option
        Pending: int64
        Overdue: int64
    }

/// Financial and work projections are loaded together but retain their independent owners.
type CasePageItem =
    {
        Case: StoredCase
        WorkSummary: CaseWorkSummary option
    }

type WorkProjection =
    {
        Snapshot: CaseWorkSnapshot
        ChangedAt: DateTimeOffset
        RecordedBy: ActorId
    }

type ReminderProjection =
    {
        Effects: ReminderEffects
        ChangedAt: DateTimeOffset
        RecordedBy: ActorId
    }

[<RequireQualifiedAccess>]
type WorkChangeProjection =
    | Work of WorkProjection
    | Reminder of ReminderProjection

[<RequireQualifiedAccess>]
type ReceiptCurrent =
    | Case of StoredCase option
    | Work of parent: StoredCase option * current: StoredWork option
    | Reminder of parent: StoredCase option * current: Map<ReminderId, StoredReminder>
    | Authority

[<RequireQualifiedAccess>]
type CaseProjection =
    | Insert of caseId: CaseId * snapshot: CaseSnapshot * index: CaseIndex
    | Update of caseId: CaseId * snapshot: CaseSnapshot * index: CaseIndex
    | Erase of caseId: CaseId * revision: Revision * changedAt: DateTimeOffset

type EntryDraft =
    {
        Effects: LedgerEffects
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
    | SetProfile of actorId: ActorId * profile: ActorProfile
    | EraseProfile of actorId: ActorId * eraseSequence: Sequence
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

type ReminderPosition =
    {
        Due: CalendarDate
        ReminderId: ReminderId
    }

type ReminderPageQuery =
    {
        Selection: CaseSelection
        TargetCaseId: CaseId option
        State: ReminderState option
        View: ReminderView option
        Due: ReminderDue option
        Text: CaseSearchText option
        IncludeInactive: bool
        After: ReminderPosition option
        Limit: int
    }

type ReminderRow =
    {
        Stored: StoredReminder
        Reference: CaseReference
        Lifecycle: Lifecycle
        Status: CaseStatus option
    }

type ActivityPageQuery =
    {
        CaseId: CaseId
        BeforeSequence: Sequence option
        Limit: int
    }

type TimelinePageQuery =
    {
        CaseId: CaseId
        Filter: TimelineFilter option
        Range: ChangeRange option
        BeforeSequence: Sequence option
        Limit: int
    }

/// Independently owned global facts observed together by read consumers that require both.
type GlobalReadFacts =
    {
        Authority: AuthorityView
        Maintenance: RegisterMaintenance
    }

type IReadTx =
    abstract ObservedAt: DateTimeOffset
    abstract Maintenance: unit -> Task<RegisterMaintenance>
    abstract GlobalReadFacts: PrincipalKey -> Task<GlobalReadFacts>
    abstract Authority: AuthorityQuery -> Task<AuthorityView>
    abstract AuthorityCases: PrincipalKey * CaseId list -> Task<AuthorityView>
    abstract FindCase: CaseId -> Task<StoredCase option>
    abstract FindCaseByReference: string -> Task<StoredCase option>
    abstract FindWork: CaseId -> Task<StoredWork option>
    abstract FindReminder: ReminderId -> Task<StoredReminder option>
    abstract WorkSummaries: CaseId list * DateOnly -> Task<Map<CaseId, CaseWorkSummary>>
    abstract AssignedAccounts: (CaseId * ActorId) list -> Task<Map<CaseId, ActorRef>>
    abstract ReminderPage: ReminderPageQuery -> Task<Page<ReminderRow, ReminderPosition>>
    abstract WorkOverview: CaseSelection * bool -> Task<WorkOverview>
    abstract CaseViewCounts: CaseSelection -> Task<CaseViewCount list>
    abstract ReminderViewCounts: CaseSelection -> Task<ReminderViewCount list>
    abstract ActivityPage: ActivityPageQuery -> Task<Page<StoredEntry, Sequence>>
    abstract TimelinePage: TimelinePageQuery -> Task<Page<StoredEntry, Sequence>>
    abstract PreviousEntries: StoredEntry list -> Task<Map<Sequence, StoredEntry>>
    abstract FindOperation: OperationId -> Task<StoredOperation option>
    abstract NonAcceptance: FenceKey -> Task<NonAcceptanceEvidence>

    abstract CasePage: CasePageQuery -> Task<Page<CasePageItem, CaseListPosition>>
    abstract CaseSummary: CaseSelection -> Task<CaseWorklistSummary>

    abstract HistoryPage: HistoryPageQuery -> Task<CaseHistorySlice>
    abstract AuthorityHistoryPage: AuthorityHistoryPageQuery -> Task<Page<StoredEntry, Sequence>>
    abstract ActorPage: ActorPageQuery -> Task<Page<Actor, ActorId>>

    abstract GrantPage: GrantPageQuery -> Task<Page<ScopedGrant, GrantPosition>>

    abstract ActorRefs: ActorId list -> Task<Map<ActorId, ActorRef>>
    abstract FindActor: ActorId -> Task<Actor option>
    abstract AssigneePage: AssigneePageQuery -> Task<Page<ActorRef, ActorId>>
    abstract EnabledHumanOwners: unit -> Task<int>
    abstract ActorCount: unit -> Task<int>

type IWriteTx =
    inherit IReadTx
    abstract Head: LedgerPosition
    abstract NewPayloadNonce: unit -> byte array
    abstract Append: EntryDraft -> Task<unit>
    abstract ProjectCase: CaseProjection -> Task<unit>
    abstract ProjectAuthority: AuthorityProjection -> Task<unit>
    abstract ProjectWorkChange: WorkChangeProjection -> Task<unit>
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

/// Context cache writes are non-ledger facts. Implementations serialize the principal before
/// taking the authority snapshot and refresh a request only when that snapshot has no actor.
[<NoComparison>]
type ContextQuery =
    {
        Principal: PrincipalKey
        Claims: RequesterClaims
        CancellationToken: CancellationToken
    }

type ContextFacts =
    {
        Facts: GlobalReadFacts
        ObservedAt: DateTimeOffset
    }

type IStore =
    abstract Context: ContextQuery -> Task<Result<ContextFacts, StoreFailure>>
    abstract Read: ReadOperation<'result> -> Task<Result<'result, StoreFailure>>
    abstract Write: WriteOperation<'result> -> Task<Result<Verdict<'result>, StoreFailure>>
