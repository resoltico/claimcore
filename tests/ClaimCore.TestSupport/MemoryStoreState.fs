module internal ClaimCore.TestSupport.MemoryStoreState

open ClaimCore.RecordFormat
open System
open System.Threading
open System.Threading.Tasks
open System.Security.Cryptography
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Protocol
open ClaimCore.Domain


type internal State =
    {
        Maintenance: RegisterMaintenance
        Head: LedgerPosition
        Entries: Map<OperationId * int, StoredEntry>
        Controls: Map<Sequence, RetainedControl>
        Cases: Map<CaseId, StoredCase>
        Work: Map<CaseId, StoredWork>
        Reminders: Map<ReminderId, StoredReminder>
        Requests: Map<PrincipalKey, AccessRequest>
        Actors: Map<ActorId, Actor>
        Grants: Set<ActorId * Role * CaseId option>
        Fences: Map<ActorId * OperationId, DateTimeOffset>
    }

let internal compareId format left right =
    StringComparer.Ordinal.Compare(format left, format right)

let internal page limit position items =
    let visible = List.truncate limit items

    {
        Items = visible
        Next =
            if List.length items > limit then
                List.tryLast visible |> Option.map position
            else
                None
    }

let internal copyEntry (entry: StoredEntry) =
    { entry with
        EntryHash = Array.copy entry.EntryHash
        PayloadCommitment = Array.copy entry.PayloadCommitment
        Payload = entry.Payload |> Option.map Array.copy
        PayloadNonce = entry.PayloadNonce |> Option.map Array.copy
    }

exception CorruptProjection

let internal readCase (entries: Map<OperationId * int, StoredEntry>) (record: StoredCase) =
    let identity =
        entries
        |> Map.tryFind ((OperationId.create (CaseId.value record.CaseId) |> Option.get), 0)
        |> Option.exists (fun entry ->
            entry.CreationReference = Some record.Snapshot.Reference
            && entry.Subject
               |> function
                   | EntryTarget.Case(id, _, CaseActKind.Open, _) -> id = record.CaseId
                   | _ -> false)

    let valid =
        match record.Snapshot.State with
        | CaseState.Erased -> Array.isEmpty record.SnapshotBytes
        | CaseState.Active _
        | CaseState.Voided _ ->
            match CaseRecord.decodeSnapshot record.SnapshotBytes with
            | Ok snapshot -> snapshot = record.Snapshot
            | Error _ -> false

    if not identity || record.Snapshot.CaseId <> record.CaseId || not valid then
        raise CorruptProjection

    { record with
        SnapshotBytes = Array.copy record.SnapshotBytes
    }


let internal pendingReminders state caseId =
    state.Reminders
    |> Map.values
    |> Seq.filter (fun row -> row.CaseId = caseId)
    |> Seq.choose _.Snapshot
    |> Seq.filter (fun reminder -> Reminder.state reminder = ReminderState.Pending)
    |> Seq.sortWith (fun left right ->
        let due = compare (Reminder.due left) (Reminder.due right)

        if due <> 0 then
            due
        else
            compareId ReminderId.format (Reminder.id left) (Reminder.id right))
    |> Seq.toList

let internal reindex state (record: StoredCase) =
    let work = state.Work |> Map.tryFind record.CaseId |> Option.bind _.Snapshot
    let next = pendingReminders state record.CaseId |> List.tryHead
    let facts = CaseIndex.create record.Index.ChangedAt record.Snapshot

    { record with
        Index =
            { facts with
                Responsible = work |> Option.bind (fun value -> value.Work.Responsible)
                FollowUp = next |> Option.map Reminder.due
                SearchText = facts.SearchText
                WorkSearchText =
                    work
                    |> Option.bind (fun value -> value.Work.Summary)
                    |> Option.map (HandlingSummary.format >> CaseSearch.normalize)
            }
    }


let internal assignedAccount state caseId actorId =
    match Map.tryFind actorId state.Actors with
    | None -> raise CorruptProjection
    | Some actor ->
        let grants =
            state.Grants
            |> Seq.choose (fun (id, role, scope) ->
                if id = actorId && (scope.IsNone || scope = Some caseId) then
                    Some
                        {
                            ActorGrant.Role = role
                            Scope =
                                scope
                                |> Option.map Scope.Case
                                |> Option.defaultValue Scope.Installation
                        }
                else
                    None)
            |> Seq.toList

        let authority =
            AuthorityView.ofActor (
                Some
                    {
                        ActorAuthority.Actor = actor
                        Grants = grants
                    }
            )

        let available =
            PrincipalKey.isHuman actor.Principal
            && Authority.can actor.Principal authority Capability.ReadCase (Scope.Case caseId)
            && Authority.can actor.Principal authority Capability.ManageCaseWork (Scope.Case caseId)

        let availability =
            if not actor.Enabled then ActorAvailability.Disabled
            elif available then ActorAvailability.Available
            else ActorAvailability.NotEligible

        Actor.reference availability actor

let internal completeOperations previousHead (state: State) =
    let changed =
        state.Entries
        |> Map.values
        |> Seq.filter (fun entry -> entry.Sequence > previousHead)
        |> Seq.map _.OperationId
        |> Set.ofSeq

    changed
    |> Set.forall (fun operation ->
        let entries =
            state.Entries
            |> Map.values
            |> Seq.filter (fun entry -> entry.OperationId = operation)
            |> Seq.sortBy (fun entry -> LedgerEffects.index entry.Effects)
            |> Seq.toList

        match StoredOperation.create entries with
        | Error _ -> false
        | Ok operation ->
            operation.Entries.Length = 1
            || (operation.Entries
                |> List.forall (fun entry ->
                    match entry.Payload, entry.PayloadNonce, entry.Subject with
                    | Some _, Some _, _ -> true
                    | None, None, EntryTarget.Authority(id, AuthorityKind.SetProfile) ->
                        state.Entries
                        |> Map.values
                        |> Seq.exists (fun erased ->
                            erased.Subject =
                                EntryTarget.Authority(id, AuthorityKind.EraseProfile)
                            && erased.Sequence > entry.Sequence)
                    | _ -> false)))
