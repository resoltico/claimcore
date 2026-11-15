namespace ClaimCore.Application.Storage

open ClaimCore.Application.Execution

open ClaimCore.Application


open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

[<RequireQualifiedAccess>]
type internal StoreFailure =
    | Internal
    | Unavailable
    | SchemaMismatch
    | Integrity
    | Cancelled
    | CommitOutcomeUnknown

type internal StoredCase =
    {
        CaseId: CaseId
        Snapshot: CaseSnapshot
        SnapshotBytes: byte array
        Index: CaseIndex
    }

type internal WorkEntryMetadata =
    {
        CaseId: CaseId
        Revision: Revision
        Act: WorkActKind
        BusinessDate: DateOnly
    }

type internal ReminderEntryMetadata =
    {
        CaseId: CaseId
        ReminderId: ReminderId
        Revision: Revision
        Act: ReminderActKind
        NextReminderId: ReminderId option
        BusinessDate: DateOnly
    }

type internal NoteEntryMetadata =
    {
        CaseId: CaseId
        NoteId: NoteId
        Act: NoteActKind
        BusinessDate: DateOnly
    }

[<RequireQualifiedAccess>]
type internal EntryTarget =
    | Case of caseId: CaseId * revision: Revision * act: CaseActKind * businessDate: DateOnly
    | People of targetActorId: ActorId * act: PeopleActKind
    | Work of WorkEntryMetadata
    | Reminder of ReminderEntryMetadata
    | Note of NoteEntryMetadata

/// A logical effect is inseparable from its closed, decoded operation root.
[<NoComparison>]
type internal StoredEffect =
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
        | EffectDescriptor.People(id, change) ->
            let act =
                match change with
                | PeopleEffectDescriptor.ProvisionOwner _ -> PeopleActKind.ProvisionOwner
                | PeopleEffectDescriptor.Register _ -> PeopleActKind.Register
                | PeopleEffectDescriptor.SetProfile _ -> PeopleActKind.SetProfile
                | PeopleEffectDescriptor.EraseProfile -> PeopleActKind.EraseProfile
                | PeopleEffectDescriptor.Grant _ -> PeopleActKind.Grant
                | PeopleEffectDescriptor.Revoke _ -> PeopleActKind.Revoke
                | PeopleEffectDescriptor.Enable -> PeopleActKind.Enable
                | PeopleEffectDescriptor.Disable -> PeopleActKind.Disable

            EntryTarget.People(id, act)
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


module internal StoredEffect =
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
type internal CaseStageFacts =
    {
        Revision: Revision
        OpenedOn: CalendarDate
        ClosedOn: CalendarDate option
        LastAct: CaseActKind
        DispositionEntry: StoredEffect option
        RecordedAt: DateTimeOffset
        RecordedBy: ActorId
    }

type internal StoredWork =
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

type internal StoredNote =
    {
        Note: Note
        CreatedPosition: EffectPosition
        RecordedAt: DateTimeOffset
        RecordedBy: ActorId
    }



type internal NotePageRows =
    {
        Items: StoredNote list
        Count: int64
        Next: EffectPosition option
    }

type internal StoredReminder =
    {
        ReminderId: ReminderId
        CaseId: CaseId
        Revision: Revision
        Snapshot: Reminder option
        SnapshotBytes: byte array
        RecordedAt: DateTimeOffset
        RecordedBy: ActorId
    }

type internal CaseWorkSummary =
    {
        Work: StoredWork
        Next: Reminder option
        Pending: int64
        Overdue: int64
    }

/// Financial and work projections are loaded together but retain their independent owners.
type internal CasePageItem =
    {
        Case: StoredCase
        WorkSummary: CaseWorkSummary option
    }

[<RequireQualifiedAccess>]
type internal WorkChangeProjection =
    | Work of WorkSubjectRows
    | Reminder of ReminderSubjectRows list
    | Note of NoteSubjectRows

[<RequireQualifiedAccess>]
type internal ReceiptCurrent =
    | Case of StoredCase option
    | Work of
        parent: StoredCase option *
        current: StoredWork option *
        accounts: Map<CaseId * ActorId, ActorRef>
    | Reminder of parent: StoredCase option * current: Map<ReminderId, StoredReminder>
    | Note of parent: StoredCase option * current: Note option
    | People of target: ActorRef option

type internal CaseProjection =
    {
        Rows: CaseSubjectRows
        Index: CaseIndex
        Family: CaseFamilyRows option
    }













type internal GrantRow =
    {
        Grant: ScopedGrant
        GrantedAt: DateTimeOffset
        GrantedBy: ActorId
    }



/// A page carries only rows and a continuation position from the same transaction snapshot.
type internal Page<'Item, 'Position> =
    {
        Items: 'Item list
        Next: 'Position option
    }

type internal ReminderPosition =
    {
        Due: CalendarDate
        ReminderId: ReminderId
    }



type internal ReminderRow =
    {
        Stored: StoredReminder
        Reference: CaseReference
        ClaimantName: PartyName
        Responsible: ActorId option
        Lifecycle: Lifecycle
        Status: CaseStatus option
    }



/// Independently owned global facts observed together by read consumers that require both.
type internal GlobalReadFacts =
    {
        People: AccessView
        AssignedRoles: ActorRoleSummary list
        Maintenance: RegisterMaintenance
    }
