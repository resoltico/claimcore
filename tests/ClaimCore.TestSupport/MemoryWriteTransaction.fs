module internal ClaimCore.TestSupport.MemoryWriteTransaction

open ClaimCore.RecordFormat
open System
open System.Threading
open System.Threading.Tasks
open System.Security.Cryptography
open ClaimCore.Application
open ClaimCore.Protocol
open ClaimCore.Domain

open ClaimCore.TestSupport.MemoryStoreState
open ClaimCore.TestSupport.MemoryReadTransaction

[<NoEquality; NoComparison>]
type internal WriteInputs =
    {
        Get: unit -> State
        Set: State -> unit
        InstallationId: Guid
        Observed: DateTimeOffset
        Head: LedgerPosition
        Nonce: byte array
        Read: unit -> unit
        FactRead: AuthorityFact -> unit
    }

type internal Writer(inputs: WriteInputs) =
    inherit
        Reader(
            inputs.Get,
            inputs.InstallationId,
            inputs.Observed,
            inputs.Read,
            factRead = inputs.FactRead
        )

    let get = inputs.Get
    let set = inputs.Set
    let head = inputs.Head
    let nonce = inputs.Nonce

    interface IWriteTx with
        member _.Head = head
        member _.Nonce = nonce

        member _.Append draft =
            let state = get ()

            let control =
                match draft.Subject with
                | EntryTarget.NonAcceptance _ -> true
                | _ -> false

            if
                draft.Sequence <> Sequence.successor state.Head.Sequence
                || draft.PreviousHash <> EntryHash.toBytes state.Head.EntryHash
                || (not control && Map.containsKey draft.OperationId state.Entries)
            then
                invalidOp "An append must extend the owned head with a fresh operation."

            set
                { state with
                    Head =
                        {
                            Sequence = draft.Sequence
                            EntryHash = EntryHash.ofBytes draft.EntryHash |> Option.get
                        }
                    Controls =
                        if control then
                            Map.add
                                draft.Sequence
                                {
                                    RetainedControl.Entry = LedgerEntries.stored draft |> copyEntry
                                    PreviousHash =
                                        EntryHash.ofBytes draft.PreviousHash |> Option.get
                                }
                                state.Controls
                        else
                            state.Controls
                    Entries =
                        if control then
                            state.Entries
                        else
                            Map.add
                                draft.OperationId
                                (LedgerEntries.stored draft |> copyEntry)
                                state.Entries
                }

            Task.FromResult(())

        member _.ProjectCase projection =
            let state = get ()

            let cases =
                match projection with
                | CaseProjection.Insert(id, snapshot, index)
                | CaseProjection.Update(id, snapshot, index) ->
                    Map.add
                        id
                        {
                            CaseId = id
                            Snapshot = snapshot
                            Index = index
                            SnapshotBytes = CaseRecord.encodeSnapshot snapshot
                        }
                        state.Cases
                | CaseProjection.Erase(id, revision, changedAt) ->
                    Map.add
                        id
                        {
                            CaseId = id
                            SnapshotBytes = Array.empty
                            Index =
                                {
                                    Responsible = None
                                    FollowUp = None
                                    Status = None
                                    Progress = None
                                    SearchText = None
                                    NotifiedOn = None
                                    Claimed = None
                                    Payable = None
                                    ChangedAt = changedAt
                                }
                            Snapshot =
                                { state.Cases[id].Snapshot with
                                    Revision = revision
                                    State = CaseState.Erased
                                }
                        }
                        state.Cases

            let parentId =
                match projection with
                | CaseProjection.Insert(id, _, _)
                | CaseProjection.Update(id, _, _)
                | CaseProjection.Erase(id, _, _) -> id

            let erased =
                match projection with
                | CaseProjection.Erase _ -> true
                | _ -> false

            let work =
                match projection with
                | CaseProjection.Insert(id, _, _) ->
                    if Map.containsKey id state.Work then
                        invalidOp "A work root already exists."

                    let root =
                        {
                            CaseWorkSnapshot.CaseId = id
                            Revision = Revision.zero
                            Work = CaseWork.empty
                        }

                    Map.add
                        id
                        {
                            StoredWork.CaseId = id
                            Revision = Revision.zero
                            Snapshot = Some root
                            SnapshotBytes = WorkRecord.encodeSnapshot root
                            ChangedAt = None
                            RecordedBy = None
                        }
                        state.Work
                | CaseProjection.Erase(id, _, _) ->
                    state.Work
                    |> Map.change
                        id
                        (Option.map (fun row ->
                            { row with
                                Snapshot = None
                                SnapshotBytes = Array.empty
                            }))
                | _ -> state.Work

            let reminders =
                if erased then
                    state.Reminders
                    |> Map.map (fun _ row ->
                        if row.CaseId = parentId then
                            { row with
                                Snapshot = None
                                SnapshotBytes = Array.empty
                            }
                        else
                            row)
                else
                    state.Reminders

            let entries =
                if erased then
                    state.Entries
                    |> Map.map (fun _ entry ->
                        let parent =
                            match entry.Subject with
                            | EntryTarget.Case(id, _, _, _) -> Some id
                            | EntryTarget.Work work -> Some work.CaseId
                            | EntryTarget.Reminder reminder -> Some reminder.CaseId
                            | _ -> None

                        if parent = Some parentId then
                            { entry with
                                Payload = None
                                PayloadNonce = None
                            }
                        else
                            entry)
                else
                    state.Entries

            let next =
                { state with
                    Cases = cases
                    Work = work
                    Reminders = reminders
                    Entries = entries
                }

            let next =
                { next with
                    Cases = Map.change parentId (Option.map (reindex next)) next.Cases
                }

            set next
            Task.FromResult(())

        member _.ProjectWorkChange projection =
            let state = get ()

            let changed, parentId =
                match projection with
                | WorkChangeProjection.Work update ->
                    let snapshot = update.Snapshot
                    let before = state.Work[snapshot.CaseId]

                    if
                        before.Snapshot.IsNone
                        || snapshot.Revision <> Revision.successor before.Revision
                    then
                        raise CorruptProjection

                    let work =
                        {
                            StoredWork.CaseId = snapshot.CaseId
                            Revision = snapshot.Revision
                            Snapshot = Some snapshot
                            SnapshotBytes = WorkRecord.encodeSnapshot snapshot
                            ChangedAt = Some update.ChangedAt
                            RecordedBy = Some update.RecordedBy
                        }

                    { state with
                        Work = Map.add snapshot.CaseId work state.Work
                    },
                    snapshot.CaseId
                | WorkChangeProjection.Reminder update ->
                    let put isNew (reminder: Reminder) (rows: Map<ReminderId, StoredReminder>) =
                        let id = Reminder.id reminder
                        let parent = Reminder.caseId reminder

                        match Map.tryFind id rows with
                        | Some _ when isNew -> raise CorruptProjection
                        | None when not isNew -> raise CorruptProjection
                        | Some previous when
                            previous.CaseId <> parent
                            || previous.Snapshot.IsNone
                            || Reminder.revision reminder <> Revision.successor previous.Revision
                            ->
                            raise CorruptProjection
                        | _ -> ()

                        if
                            not (Map.containsKey parent state.Cases)
                            || CaseState.lifecycle state.Cases[parent].Snapshot.State =
                                Lifecycle.Erased
                        then
                            raise CorruptProjection

                        Map.add
                            id
                            {
                                StoredReminder.ReminderId = id
                                CaseId = parent
                                Revision = Reminder.revision reminder
                                Snapshot = Some reminder
                                SnapshotBytes = ReminderRecord.encodeSnapshot reminder
                                RecordedAt = update.ChangedAt
                                RecordedBy = update.RecordedBy
                            }
                            rows

                    let rows, parent =
                        match update.Effects with
                        | ReminderEffects.Created reminder ->
                            put true reminder state.Reminders, Reminder.caseId reminder
                        | ReminderEffects.Changed reminder ->
                            put false reminder state.Reminders, Reminder.caseId reminder
                        | ReminderEffects.CompletedAndPlanned(completed, next) ->
                            if Reminder.caseId completed <> Reminder.caseId next then
                                raise CorruptProjection

                            state.Reminders |> put false completed |> put true next,
                            Reminder.caseId completed

                    { state with Reminders = rows }, parent

            let updated =
                { changed with
                    Cases = Map.change parentId (Option.map (reindex changed)) changed.Cases
                }

            set updated
            Task.FromResult(())

        member _.ProjectAuthority projection =
            let state = get ()

            let changed =
                match projection with
                | AuthorityProjection.Register(id, principal) ->
                    { state with
                        Actors =
                            Map.add
                                id
                                {
                                    ActorId = id
                                    Principal = principal
                                    Enabled = true
                                }
                                state.Actors
                    }
                | AuthorityProjection.ProvisionOwner(id, principal) ->
                    { state with
                        Actors =
                            Map.add
                                id
                                {
                                    ActorId = id
                                    Principal = principal
                                    Enabled = true
                                }
                                state.Actors
                        Grants = Set.add (id, Role.Owner, None) state.Grants
                    }
                | AuthorityProjection.SetEnabled(id, enabled) ->
                    { state with
                        Actors =
                            Map.add
                                id
                                { state.Actors[id] with
                                    Enabled = enabled
                                }
                                state.Actors
                    }
                | AuthorityProjection.AddGrant(id, role, scope) ->
                    { state with
                        Grants = Set.add (id, role, scope) state.Grants
                    }
                | AuthorityProjection.RemoveGrant(id, role, scope) ->
                    { state with
                        Grants = Set.remove (id, role, scope) state.Grants
                    }

            set changed
            Task.FromResult(())

        member _.Fence
            ({
                 FenceKey.ActorId = actor
                 OperationId = operation
             })
            =
            set
                { get () with
                    Fences =
                        let existing = (get ()).Fences

                        if Map.containsKey (actor, operation) existing then
                            existing
                        else
                            Map.add (actor, operation) inputs.Observed existing
                }

            Task.FromResult(())

/// One transactional double for Application tests; refusals and replay discard pending state.
