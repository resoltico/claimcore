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

/// A logical part version is inseparable from its complete, decoded accepted root.
[<NoComparison>]
type internal StoredEffect =
    private
        {
            OperationValue: RetainedOperation
            IndexValue: int
        }

    member this.Operation = this.OperationValue
    member this.Index = this.IndexValue
    member this.Change = this.OperationValue.Changes[this.IndexValue]

    member this.Id =
        EffectId.create this.OperationValue.Root.Header.OperationId this.IndexValue
        |> Option.get

    member this.Position = this.Change.Stamp.Position
    member this.Sequence = this.OperationValue.Root.Header.Sequence
    member this.EntryHash = EntryHash.toBytes this.OperationValue.Root.Hash
    member this.OperationId = this.OperationValue.Root.Header.OperationId
    member this.Authority = this.OperationValue.Root.Header.Authority
    member this.RecordedAt = this.OperationValue.Root.Header.RecordedAt
    member this.Task = this.OperationValue.Root.Structure.Task

module internal StoredEffect =
    let ofOperation (operation: RetainedOperation) =
        operation.Changes
        |> List.mapi (fun index _ ->
            {
                OperationValue = operation
                IndexValue = index
            })

    let privateAvailable (entry: StoredEffect) =
        match entry.Operation.Root.Structure.Effects[entry.Index].Compartment with
        | None -> true
        | Some ordinal ->
            entry.Operation.Root.Structure.Compartments[ordinal]
            |> Compartment.content
            |> Option.isSome

    let privateUnavailable entry = not (privateAvailable entry)

    let find index operation =
        ofOperation operation |> List.tryItem index

[<NoComparison>]
type internal CaseStageFacts =
    {
        Revision: Revision
        OpenedOn: CalendarDate
        ClosedOn: CalendarDate option
        Snapshot: CaseSnapshot
        Disposition: CaseDisposition option
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

type internal GrantRow =
    {
        Grant: ScopedGrant
        GrantedAt: DateTimeOffset
        GrantedBy: Authority
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
        ClaimantName: PartyName option
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
