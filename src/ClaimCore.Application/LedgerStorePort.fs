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
        Paid: Money option
    }

module CaseIndex =
    let create changedAt (snapshot: CaseSnapshot) =
        let fields = CaseState.fields snapshot.State

        {
            Responsible = None
            FollowUp = None
            Status = fields |> Option.map _.Status
            Progress = fields |> Option.map (fun fields -> CaseProgress.ofFinancial fields.Finance)
            ChangedAt = changedAt
            SearchText = fields |> Option.map CaseSearch.text
            WorkSearchText = None
            NotifiedOn =
                fields
                |> Option.map (fun fields ->
                    Registration.incidentNotificationDate fields.Registration)
            Claimed = fields |> Option.map (fun fields -> Registration.claimed fields.Registration)
            Paid =
                fields
                |> Option.bind (fun fields ->
                    FinancialPosition.totals fields.Finance |> Option.map _.Paid)
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

type WorkEntryMetadata =
    {
        CaseId: CaseId
        Revision: Revision
        Act: WorkActKind
        BusinessDate: DateOnly
    }

type ReminderEntryMetadata =
    {
        CaseId: CaseId
        ReminderId: ReminderId
        Revision: Revision
        Act: ReminderActKind
        NextReminderId: ReminderId option
        BusinessDate: DateOnly
    }

type NoteEntryMetadata =
    {
        CaseId: CaseId
        NoteId: NoteId
        Act: NoteActKind
        BusinessDate: DateOnly
    }

[<RequireQualifiedAccess>]
type EntryTarget =
    | Case of caseId: CaseId * revision: Revision * act: CaseActKind * businessDate: DateOnly
    | Authority of targetActorId: ActorId * act: AuthorityKind
    | Work of WorkEntryMetadata
    | Reminder of ReminderEntryMetadata
    | Note of NoteEntryMetadata

/// A logical effect is inseparable from its closed, decoded operation root.
[<NoComparison>]
type StoredEffect =
    private
        {
            OperationValue: RetainedOperation
            IndexValue: int
        }

    member this.Operation = this.OperationValue
    member this.Index = this.IndexValue
    member this.Descriptor = this.OperationValue.Root.Structure.Effects[this.IndexValue]

    member this.Id =
        EffectId.create this.OperationValue.Root.Header.OperationId this.IndexValue
        |> Option.get

    member this.Position =
        EffectPosition.create this.OperationValue.Root.Header.Sequence this.IndexValue
        |> Option.get

    member this.Sequence = this.OperationValue.Root.Header.Sequence
    member this.EntryHash = EntryHash.toBytes this.OperationValue.Root.Hash
    member this.OperationId = this.OperationValue.Root.Header.OperationId
    member this.ActorId = this.OperationValue.Root.Header.ActorId
    member this.RecordedAt = this.OperationValue.Root.Header.RecordedAt

    member this.CreationReference =
        match this.Descriptor with
        | EffectDescriptor.Case(_, _, _, reference, _) -> reference
        | _ -> None

    member this.Subject =
        let businessDate () =
            this.OperationValue.Root.Header.BusinessDate.Value

        match this.Descriptor with
        | EffectDescriptor.Case(id, revision, act, _, _) ->
            EntryTarget.Case(id, revision, act, businessDate ())
        | EffectDescriptor.Authority(id, change) ->
            let act =
                match change with
                | AuthorityEffectDescriptor.ProvisionOwner _ -> AuthorityKind.ProvisionOwner
                | AuthorityEffectDescriptor.Register _ -> AuthorityKind.Register
                | AuthorityEffectDescriptor.SetProfile _ -> AuthorityKind.SetProfile
                | AuthorityEffectDescriptor.EraseProfile -> AuthorityKind.EraseProfile
                | AuthorityEffectDescriptor.Grant _ -> AuthorityKind.Grant
                | AuthorityEffectDescriptor.Revoke _ -> AuthorityKind.Revoke
                | AuthorityEffectDescriptor.Enable -> AuthorityKind.Enable
                | AuthorityEffectDescriptor.Disable -> AuthorityKind.Disable

            EntryTarget.Authority(id, act)
        | EffectDescriptor.Work(parent, revision) ->
            EntryTarget.Work
                {
                    CaseId = parent
                    Revision = revision
                    Act = WorkActKind.UpdateWork
                    BusinessDate = businessDate ()
                }
        | EffectDescriptor.NoteAdded(parent, id) ->
            EntryTarget.Note
                {
                    CaseId = parent
                    NoteId = id
                    Act = NoteActKind.AddNote
                    BusinessDate = businessDate ()
                }
        | EffectDescriptor.NoteRemoved(parent, id) ->
            EntryTarget.Note
                {
                    CaseId = parent
                    NoteId = id
                    Act = NoteActKind.RemoveNote
                    BusinessDate = businessDate ()
                }
        | EffectDescriptor.ReminderCreated(parent, id, revision) ->
            EntryTarget.Reminder
                {
                    CaseId = parent
                    ReminderId = id
                    Revision = revision
                    Act = ReminderActKind.Create
                    NextReminderId = None
                    BusinessDate = businessDate ()
                }
        | EffectDescriptor.ReminderChanged(parent, id, revision, act) ->
            let compound =
                match this.OperationValue.Root.Structure.Command with
                | CommandSkeleton.Reminder command ->
                    command.Act = ReminderActKind.CompleteAndPlanNext && this.IndexValue = 0
                | _ -> false

            let kind =
                if compound then
                    ReminderActKind.CompleteAndPlanNext
                else
                    act

            let next =
                if compound then
                    Some(ReminderId.ofOperationId this.OperationId)
                else
                    None

            EntryTarget.Reminder
                {
                    CaseId = parent
                    ReminderId = id
                    Revision = revision
                    Act = kind
                    NextReminderId = next
                    BusinessDate = businessDate ()
                }


module StoredEffect =
    let ofOperation (operation: RetainedOperation) =
        operation.Root.Structure.Effects
        |> List.mapi (fun index _ ->
            {
                OperationValue = operation
                IndexValue = index
            })

    let privateAvailable (entry: StoredEffect) =
        entry.Operation.Root.Structure.Compartments
        |> List.exists (Compartment.content >> Option.isSome)

    let privateUnavailable (entry: StoredEffect) =
        entry.Operation.Root.Structure.Compartments
        |> List.exists (Compartment.content >> Option.isNone)

    let find index operation =
        ofOperation operation |> List.tryItem index

[<NoComparison>]
type CaseStageFacts =
    {
        Revision: Revision
        OpenedOn: CalendarDate
        ClosedOn: CalendarDate option
        LastAct: CaseActKind
        DispositionEntry: StoredEffect option
        RecordedAt: DateTimeOffset
        RecordedBy: ActorId
    }

type StoredWork =
    {
        CaseId: CaseId
        Revision: Revision
        Snapshot: CaseWorkSnapshot option
        SnapshotBytes: byte array
        ChangedAt: DateTimeOffset option
        RecordedBy: ActorId option
        SummaryChangedAt: DateTimeOffset option
        SummaryRecordedBy: ActorId option
    }

type StoredNote =
    {
        Note: Note
        CreatedPosition: EffectPosition
        RecordedAt: DateTimeOffset
        RecordedBy: ActorId
    }

type NotePageQuery =
    {
        CaseId: CaseId
        Before: EffectPosition option
        Limit: int
    }

type NotePageRows =
    {
        Items: StoredNote list
        Count: int64
        Next: EffectPosition option
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
        SummaryChangedAt: DateTimeOffset option
        SummaryRecordedBy: ActorId option
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
    | Note of Note

[<RequireQualifiedAccess>]
type ReceiptCurrent =
    | Case of StoredCase option
    | Work of
        parent: StoredCase option *
        current: StoredWork option *
        accounts: Map<CaseId * ActorId, ActorRef>
    | Reminder of parent: StoredCase option * current: Map<ReminderId, StoredReminder>
    | Note of parent: StoredCase option * current: Note option
    | Authority of target: ActorRef option

[<RequireQualifiedAccess>]
type CaseProjection =
    | Insert of caseId: CaseId * snapshot: CaseSnapshot * index: CaseIndex
    | Update of
        caseId: CaseId *
        snapshot: CaseSnapshot *
        index: CaseIndex *
        payment: PaymentDifference option
    | Erase of caseId: CaseId * revision: Revision * changedAt: DateTimeOffset

[<RequireQualifiedAccess>]
type AuthorityProjection =
    | Register of actorId: ActorId * principal: PrincipalKey
    | ProvisionOwner of actorId: ActorId * principal: PrincipalKey * grantedAt: EffectPosition
    | SetProfile of actorId: ActorId * profile: ActorProfile
    | EraseProfile of actorId: ActorId * erasePosition: EffectPosition
    | SetEnabled of actorId: ActorId * enabled: bool
    | AddGrant of actorId: ActorId * role: Role * caseId: CaseId option * grantedAt: EffectPosition
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

type AuthorityHistoryPageQuery =
    {
        TargetActorId: ActorId option
        BeforePosition: EffectPosition option
        Limit: int
    }

type ActorPageQuery =
    {
        Role: Role option
        Text: DirectorySearchText option
        AfterActorId: ActorId option
        Limit: int
    }

type GrantRow =
    {
        Grant: ScopedGrant
        GrantedAt: DateTimeOffset
        GrantedBy: ActorId
    }

type GrantPageQuery =
    {
        ActorId: ActorId
        AfterGrant: GrantPosition option
        Limit: int
    }

/// A page carries only rows and a continuation position from the same transaction snapshot.
type Page<'Item, 'Position> =
    {
        Items: 'Item list
        Next: 'Position option
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
        ClaimantName: PartyName
        Responsible: ActorId option
        Lifecycle: Lifecycle
        Status: CaseStatus option
    }

type TimelinePageQuery =
    {
        CaseId: CaseId
        Filter: TimelineFilter option
        Range: ChangeRange option
        BeforePosition: EffectPosition option
        Limit: int
    }

/// Independently owned global facts observed together by read consumers that require both.
type GlobalReadFacts =
    {
        Authority: AuthorityView
        AssignedRoles: ActorRoleSummary list
        Maintenance: RegisterMaintenance
    }

type AccessRequestPageQuery =
    {
        After: AccessRequestPosition option
        ObservedAt: DateTimeOffset
        Limit: int
    }

/// Private payment content becomes unavailable after parent erasure; accepting provenance remains.
[<RequireQualifiedAccess>]
type PaymentProjectionContent =
    | Available of PaymentEntry * removed: bool
    | Cleared of removed: bool

type PaymentProjectionRow =
    {
        CaseId: CaseId
        PaymentId: OperationId
        Position: EffectPosition
        Content: PaymentProjectionContent
    }

type PaymentPageQuery =
    {
        CaseId: CaseId
        Before: EffectPosition option
        Limit: int
    }

type IReadTx =
    abstract ObservedAt: DateTimeOffset
    abstract ExpiredBefore: unit -> Task<DateTimeOffset option>
    abstract Maintenance: unit -> Task<RegisterMaintenance>
    abstract GlobalReadFacts: PrincipalKey -> Task<GlobalReadFacts>
    abstract Authority: AuthorityQuery -> Task<AuthorityView>
    abstract AuthorityCases: PrincipalKey * CaseId list -> Task<AuthorityView>
    abstract FindCase: CaseId -> Task<StoredCase option>
    abstract FindCaseByReference: string -> Task<StoredCase option>

    abstract PaymentPage:
        PaymentPageQuery -> Task<Page<PaymentEntry * EffectPosition, EffectPosition>>

    abstract LatestPayments: CaseId * int -> Task<PaymentEntry list>
    abstract ActivePayment: CaseId * Guid -> Task<PaymentEntry option>
    abstract PaymentBoundsExcluding: CaseId * OperationId -> Task<PaymentDateBounds option>
    abstract CaseStageFacts: CaseId -> Task<CaseStageFacts option>
    abstract FindWork: CaseId -> Task<StoredWork option>
    abstract FindNote: NoteId -> Task<StoredNote option>
    abstract NotePage: NotePageQuery -> Task<NotePageRows>
    abstract FindReminder: ReminderId -> Task<StoredReminder option>
    abstract WorkSummaries: CaseId list * DateOnly -> Task<Map<CaseId, CaseWorkSummary>>
    abstract AssignedAccounts: (CaseId * ActorId) list -> Task<Map<CaseId * ActorId, ActorRef>>
    abstract ResponsibilityCounts: CaseSelection * ActorId list -> Task<Map<ActorId, int64>>
    abstract ReminderPage: ReminderPageQuery -> Task<Page<ReminderRow, ReminderPosition>>
    abstract CaseViewCounts: CaseSelection -> Task<CaseViewCount list>
    abstract ReminderViewCounts: CaseSelection -> Task<ReminderViewCount list>
    abstract TimelinePage: TimelinePageQuery -> Task<Page<StoredEffect, EffectPosition>>
    abstract PreviousEntries: StoredEffect list -> Task<Map<EffectPosition, StoredEffect>>
    abstract FindOperation: OperationId -> Task<RetainedOperation option>

    abstract CasePage: CasePageQuery -> Task<Page<CasePageItem, CaseListPosition>>
    abstract CaseSummary: CaseSelection -> Task<CaseWorklistSummary>

    abstract AuthorityHistoryPage:
        AuthorityHistoryPageQuery -> Task<Page<StoredEffect, EffectPosition>>

    abstract AccessRequestPage:
        AccessRequestPageQuery -> Task<Page<AccessRequest, AccessRequestPosition>>

    abstract ActorPage: ActorPageQuery -> Task<Page<Actor, ActorId>>

    abstract GrantPage: GrantPageQuery -> Task<Page<GrantRow, GrantPosition>>

    abstract ActorRoleSummaries: ActorId list -> Task<Map<ActorId, ActorRoleSummary list>>
    abstract ActorRefs: ActorId list -> Task<Map<ActorId, ActorRef>>
    abstract FindActor: ActorId -> Task<Actor option>
    abstract AssigneePage: AssigneePageQuery -> Task<Page<ActorRef, ActorId>>
    abstract EnabledHumanOwners: unit -> Task<int>
    abstract ActorCount: unit -> Task<int>

type IWriteTx =
    inherit IReadTx
    abstract Head: LedgerPosition
    abstract NewPayloadNonce: unit -> byte array
    abstract AdvanceExpiryFrontier: DateTimeOffset -> Task<DateTimeOffset>
    abstract Append: RetainedOperation -> Task<unit>
    abstract ProjectCase: CaseProjection -> Task<unit>
    abstract DismissAccessRequest: PrincipalKey -> Task<unit>
    abstract ProjectAuthority: AuthorityProjection -> Task<unit>
    abstract ProjectWorkChange: WorkChangeProjection -> Task<unit>

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
