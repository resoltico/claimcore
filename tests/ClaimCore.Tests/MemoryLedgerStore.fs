module ClaimCore.Tests.MemoryLedgerStore

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.Application

exception private UnknownCommitFault

[<NoEquality; NoComparison>]
type private State =
    {
        Actors: Map<PrincipalKey, ActorAuthority>
        Cases: Map<string, StoredCase>
        Entries: StoredEntry list
    }

let private copyEntry (entry: StoredEntry) =
    { entry with
        EntryHash = Array.copy entry.EntryHash
        PayloadCommitment = Array.copy entry.PayloadCommitment
        Payload = entry.Payload |> Option.map Array.copy
        PayloadNonce = entry.PayloadNonce |> Option.map Array.copy
    }

let private ownerGrant: ActorGrant =
    {
        Role = Role.Owner
        Scope = GrantScope.Installation
    }

let private referenceOrder left right =
    String.CompareOrdinal(CaseReference.key left, CaseReference.key right)

let private updateActor id change state =
    let actor = state.Actors |> Map.values |> Seq.find (fun value -> value.ActorId = id)

    { state with
        Actors = state.Actors |> Map.add actor.Principal (change actor)
    }

let private registerActor id principal grants state =
    { state with
        Actors =
            state.Actors
            |> Map.add
                principal
                {
                    ActorId = id
                    Principal = principal
                    Enabled = true
                    Grants = grants
                }
    }

let private grant role caseId : ActorGrant =
    {
        Role = role
        Scope =
            caseId
            |> Option.map GrantScope.Case
            |> Option.defaultValue GrantScope.Installation
    }

let private applyAuthority projection state =
    match projection with
    | AuthorityProjection.RegisterActor(id, principal) -> registerActor id principal [] state
    | AuthorityProjection.ProvisionOwner(id, principal) ->
        registerActor id principal [ ownerGrant ] state
    | AuthorityProjection.SetEnabled(id, enabled) ->
        updateActor id (fun actor -> { actor with Enabled = enabled }) state
    | AuthorityProjection.AddGrant(id, role, caseId) ->
        updateActor
            id
            (fun actor ->
                { actor with
                    Grants = grant role caseId :: actor.Grants |> List.distinct
                })
            state
    | AuthorityProjection.RemoveGrant(id, role, caseId) ->
        updateActor
            id
            (fun actor ->
                { actor with
                    Grants = actor.Grants |> List.filter ((<>) (grant role caseId))
                })
            state

let private eraseCase id revision state =
    let original =
        state.Cases |> Map.values |> Seq.find (fun value -> value.CaseId = id)

    { state with
        Cases =
            state.Cases
            |> Map.add
                (CaseReference.key original.Snapshot.Reference)
                { original with
                    Snapshot =
                        { original.Snapshot with
                            Revision = revision
                            View = CaseView.Erased
                        }
                }
        Entries =
            state.Entries
            |> List.map (fun entry ->
                match entry.Scope with
                | StoredScope.Case(caseId, _, _, _) when caseId = id ->
                    { entry with
                        Payload = None
                        PayloadNonce = None
                    }
                | _ -> entry)
    }

let private applyCase projection state =
    match projection with
    | CaseProjection.Insert(id, snapshot) ->
        let key = CaseReference.key snapshot.Reference

        if Map.containsKey key state.Cases then
            invalidOp "Duplicate case."

        { state with
            Cases = Map.add key { CaseId = id; Snapshot = snapshot } state.Cases
        }
    | CaseProjection.Update(id, snapshot) ->
        let key = CaseReference.key snapshot.Reference
        let original = state.Cases[key]

        if original.CaseId <> id || original.Snapshot.Reference <> snapshot.Reference then
            invalidOp "Wrong case projection."

        { state with
            Cases = Map.add key { original with Snapshot = snapshot } state.Cases
        }
    | CaseProjection.Erase(id, revision) -> eraseCase id revision state

let private reviewGrant state (value: ActorGrant) : AuthorityGrant =
    let scope =
        match value.Scope with
        | GrantScope.Installation -> GrantTarget.Installation
        | GrantScope.Case id ->
            state.Cases
            |> Map.values
            |> Seq.find (fun record -> record.CaseId = id)
            |> fun record -> GrantTarget.CaseReference record.Snapshot.Reference

    { Role = value.Role; Scope = scope }

let private reviewActor (actor: ActorAuthority) : ActorSummary =
    {
        ActorId = actor.ActorId
        Principal = actor.Principal
        Enabled = actor.Enabled
    }

let private grantPosition (g: ActorGrant) : GrantPosition =
    {
        Role = g.Role
        CaseId =
            match g.Scope with
            | GrantScope.Installation -> None
            | GrantScope.Case id -> Some id
    }

let private compareGrantPosition (left: GrantPosition) (right: GrantPosition) =
    let role = String.CompareOrdinal(Roles.token left.Role, Roles.token right.Role)

    if role <> 0 then
        role
    else
        match left.CaseId, right.CaseId with
        | None, None -> 0
        | None, Some _ -> 1
        | Some _, None -> -1
        | Some a, Some b -> String.CompareOrdinal(a.ToString("D"), b.ToString("D"))


let private historyRows (state: State) (record: StoredCase option) after limit =
    state.Entries
    |> List.filter (fun entry ->
        match entry.Scope, record with
        | StoredScope.Case(id, revision, _, _), Some record ->
            id = record.CaseId && revision > after
        | _ -> false)
    |> List.sortBy (fun entry ->
        match entry.Scope with
        | StoredScope.Case(_, revision, _, _) -> revision
        | _ -> 0L)
    |> List.truncate (limit + 1)
    |> List.map copyEntry

let private nextRevision (rows: StoredEntry list) (visible: StoredEntry list) limit =
    if List.length rows <= limit then
        None
    else
        visible
        |> List.tryLast
        |> Option.map (fun entry ->
            match entry.Scope with
            | StoredScope.Case(_, revision, _, _) -> revision
            | _ -> invalidOp "Case history required.")

type internal Store(instant: DateTimeOffset) =
    let gate = new SemaphoreSlim(1, 1)

    let mutable state =
        {
            Actors = Map.empty
            Cases = Map.empty
            Entries = []
        }

    let mutable observedAt = instant
    let mutable nextFailure: StoreFailure option = None
    let mutable unknownLands = true
    let mutable afterCommit = ignore
    let mutable calls = 0
    let mutable forcedHead: LedgerHead option = None
    let actor principal = Map.tryFind principal state.Actors

    let case reference =
        Map.tryFind (CaseReference.key reference) state.Cases

    let operation id =
        state.Entries
        |> List.tryFind (fun entry -> entry.OperationId = id)
        |> Option.map copyEntry

    let head () : LedgerHead =
        match forcedHead, state.Entries |> List.tryLast with
        | Some value, _ ->
            { value with
                EntryHash = Array.copy value.EntryHash
            }
        | _, Some entry ->
            {
                Sequence = entry.Sequence
                EntryHash = Array.copy entry.EntryHash
            }
        | _ ->
            {
                Sequence = 0L
                EntryHash = Array.zeroCreate 32
            }

    let commit (entry: EntryDraft) project =
        let head = head ()

        if
            head.Sequence < 0L
            || head.Sequence > Revision.Maximum
            || entry.Sequence < 1L
            || entry.Sequence > Revision.Maximum
            || entry.Sequence <> head.Sequence + 1L
            || entry.PreviousHash <> head.EntryHash
            || (operation entry.OperationId |> Option.isSome)
        then
            invalidOp "Invalid ledger append."

        let appended =
            { state with
                Entries = state.Entries @ [ LedgerEntries.stored entry |> copyEntry ]
            }

        let changed = project appended
        state <- changed

        try
            afterCommit ()
        with _ ->
            raise UnknownCommitFault

    let nonce () =
        [| for index in 0..31 -> byte ((state.Entries.Length + index) % 256) |]

    let owners () =
        state.Actors
        |> Map.values
        |> Seq.filter (fun actor ->
            actor.Enabled
            && PrincipalKey.isHuman actor.Principal
            && List.contains ownerGrant actor.Grants)
        |> Seq.length

    let locked (ct: CancellationToken) work =
        task {
            try
                do! gate.WaitAsync ct

                try
                    return work ()
                finally
                    gate.Release() |> ignore
            with
            | UnknownCommitFault -> return Error StoreFailure.CommitOutcomeUnknown
            | :? OperationCanceledException -> return Error StoreFailure.Cancelled
            | :? InvalidOperationException
            | :? Collections.Generic.KeyNotFoundException -> return Error StoreFailure.Integrity
        }

    let failure () =
        let value = nextFailure in
        nextFailure <- None
        value

    let write result project (failure: StoreFailure option) =
        match result with
        | LedgerDecision.Refuse error -> Ok(LedgerWriteResult.Refused error)
        | LedgerDecision.Fail fault -> Ok(LedgerWriteResult.Failed fault)
        | LedgerDecision.Replay receipt -> Ok(LedgerWriteResult.Replayed receipt)
        | LedgerDecision.Append(entry, projection, receipt) ->
            if failure.IsNone || unknownLands then
                commit entry (project projection)

            if failure.IsSome then
                Error StoreFailure.CommitOutcomeUnknown
            else
                Ok(LedgerWriteResult.Appended receipt)

    member _.Instant
        with get () = observedAt
        and set value = observedAt <- value

    member _.NextFailure
        with set value = nextFailure <- Some value

    member _.UnknownCommitLands
        with set value = unknownLands <- value

    member _.AfterCommit
        with set value = afterCommit <- value

    member _.HeadOverride
        with set value = forcedHead <- value

    member _.DecisionCalls = calls
    member _.Entries = state.Entries |> List.map copyEntry
    member _.Cases = state.Cases |> Map.values |> Seq.toList
    member _.Actors = state.Actors |> Map.values |> Seq.toList

    interface IDisposable with
        member _.Dispose() = gate.Dispose()

    interface ILedgerStore with
        member _.WriteCase(principal, id, reference, decide, ct) =
            locked ct (fun () ->
                let failure = failure ()

                match failure with
                | Some error when error <> StoreFailure.CommitOutcomeUnknown -> Error error
                | _ ->
                    calls <- calls + 1

                    let view: CaseLockedView =
                        {
                            Head = head ()
                            ObservedAt = observedAt
                            PayloadNonce = nonce ()
                            Actor = actor principal
                            ExistingOperation = operation id
                            Case = case reference
                        }

                    write (decide view) applyCase failure)

        member _.WriteAuthority(principal, id, target, reference, decide, ct) =
            locked ct (fun () ->
                let failure = failure ()

                match failure with
                | Some error when error <> StoreFailure.CommitOutcomeUnknown -> Error error
                | _ ->
                    calls <- calls + 1

                    let view: AuthorityLockedView =
                        {
                            Head = head ()
                            ObservedAt = observedAt
                            PayloadNonce = nonce ()
                            Actor = actor principal
                            ExistingOperation = operation id
                            Target = actor target
                            TargetCase = reference |> Option.bind case
                            EnabledHumanOwners = owners ()
                            ActorCount = state.Actors.Count
                        }

                    write (decide view) applyAuthority failure)

        member _.ReadCase(principal, id, reference, ct) =
            locked ct (fun () ->
                Ok
                    {
                        ObservedAt = observedAt
                        Actor = actor principal
                        ExistingOperation = id |> Option.bind operation
                        Case = case reference
                    })

        member _.ReadActor(principal, ct) =
            locked ct (fun () ->
                Ok
                    {
                        ObservedAt = observedAt
                        Actor = actor principal
                    })

        member _.ReadOperation(principal, id, ct) =
            locked ct (fun () ->
                let entry = operation id

                let current =
                    entry
                    |> Option.bind (fun value ->
                        match value.Scope with
                        | StoredScope.Case(id, _, _, _) ->
                            state.Cases
                            |> Map.values
                            |> Seq.tryFind (fun record -> record.CaseId = id)
                        | _ -> None)

                Ok
                    {
                        ObservedAt = observedAt
                        Actor = actor principal
                        Entry = entry
                        Case = current
                    })

        member _.ReadHistory(principal, reference, select, limit, ct) =
            locked ct (fun () ->
                let view: HistoryReadView =
                    {
                        ObservedAt = observedAt
                        Actor = actor principal
                        Case = case reference
                    }

                match select view with
                | Error reason -> Ok(Error reason)
                | Ok after ->
                    let rows = historyRows state view.Case after limit
                    let visible = rows |> List.truncate limit

                    Ok(
                        Ok
                            {
                                ObservedAt = view.ObservedAt
                                Actor = view.Actor
                                Case = view.Case
                                Entries = visible
                                NextAfterRevision = nextRevision rows visible limit
                            }
                    ))

        member _.ListCases(principal, select, limit, ct) =
            locked ct (fun () ->
                let view: ActorReadView =
                    {
                        ObservedAt = observedAt
                        Actor = actor principal
                    }

                match select view with
                | Error reason -> Ok(Error reason)
                | Ok selection ->
                    let rows =
                        state.Cases
                        |> Map.values
                        |> Seq.filter (fun record ->
                            (match selection.Visibility with
                             | CaseVisibility.Every -> true
                             | CaseVisibility.Only ids -> List.contains record.CaseId ids)
                            && (selection.AfterReference
                                |> Option.forall (fun after ->
                                    referenceOrder record.Snapshot.Reference after > 0)))
                        |> Seq.sortWith (fun left right ->
                            referenceOrder left.Snapshot.Reference right.Snapshot.Reference)
                        |> Seq.truncate (limit + 1)
                        |> Seq.toList

                    let items = rows |> List.truncate limit

                    let next =
                        if rows.Length > limit then
                            items
                            |> List.tryLast
                            |> Option.map (fun record ->
                                CaseReference.key record.Snapshot.Reference)
                        else
                            None

                    Ok(
                        Ok
                            {
                                ObservedAt = view.ObservedAt
                                Actor = view.Actor
                                Items = items
                                NextAfterReference = next
                            }
                    ))

        member _.ListActors(principal, select, limit, ct) =
            locked ct (fun () ->
                let view: ActorReadView =
                    {
                        ObservedAt = observedAt
                        Actor = actor principal
                    }

                match select view with
                | Error reason -> Ok(Error reason)
                | Ok after ->
                    let rows =
                        state.Actors
                        |> Map.values
                        |> Seq.filter (fun actor ->
                            after
                            |> Option.forall (fun id ->
                                String.CompareOrdinal(
                                    actor.ActorId.ToString("D"),
                                    id.ToString("D")
                                )
                                    >
                                    0))
                        |> Seq.sortWith (fun left right ->
                            String.CompareOrdinal(
                                left.ActorId.ToString("D"),
                                right.ActorId.ToString("D")
                            ))
                        |> Seq.truncate (limit + 1)
                        |> Seq.toList

                    let visible = rows |> List.truncate limit

                    let next =
                        if rows.Length > limit then
                            visible |> List.tryLast |> Option.map _.ActorId
                        else
                            None

                    Ok(
                        Ok
                            {
                                ObservedAt = view.ObservedAt
                                Actor = view.Actor
                                Items = visible |> List.map reviewActor
                                NextAfterActorId = next
                            }
                    ))

        member _.ListGrants(principal, targetId, select, limit, ct) =
            locked ct (fun () ->
                let target =
                    state.Actors |> Map.values |> Seq.tryFind (fun a -> a.ActorId = targetId)

                let view: GrantReadView =
                    {
                        ObservedAt = observedAt
                        Actor = actor principal
                        Target = target |> Option.map reviewActor
                    }

                match select view with
                | Error reason -> Ok(Error reason)
                | Ok after ->
                    let target = target |> Option.get

                    let rows =
                        target.Grants
                        |> List.sortWith (fun a b ->
                            compareGrantPosition (grantPosition a) (grantPosition b))
                        |> List.filter (fun g ->
                            after
                            |> Option.forall (fun p ->
                                compareGrantPosition (grantPosition g) p > 0))
                        |> List.truncate (limit + 1)

                    let visible = rows |> List.truncate limit

                    Ok(
                        Ok
                            {
                                ObservedAt = observedAt
                                Actor = view.Actor
                                Target = reviewActor target
                                Items = visible |> List.map (reviewGrant state)
                                NextAfterGrant =
                                    if rows.Length > limit then
                                        visible |> List.tryLast |> Option.map grantPosition
                                    else
                                        None
                            }
                    ))
