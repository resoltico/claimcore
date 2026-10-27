module internal ClaimCore.TestSupport.MemoryStore

open ClaimCore.RecordFormat
open System
open System.Threading
open System.Threading.Tasks
open System.Security.Cryptography
open ClaimCore.Application
open ClaimCore.Protocol
open ClaimCore.Domain

type private State =
    {
        Head: LedgerPosition
        Entries: Map<Guid, StoredEntry>
        Cases: Map<Guid, StoredCase>
        Actors: Map<Guid, Actor>
        Grants: Set<Guid * Role * Guid option>
        Fences: Set<Guid * Guid>
    }

let private compareId (left: Guid) (right: Guid) =
    StringComparer.Ordinal.Compare(left.ToString("N"), right.ToString("N"))

let private page limit position items =
    let visible = List.truncate limit items

    {
        Items = visible
        Next =
            if List.length items > limit then
                List.tryLast visible |> Option.map position
            else
                None
    }

let private copyEntry (entry: StoredEntry) =
    { entry with
        EntryHash = Array.copy entry.EntryHash
        PayloadCommitment = Array.copy entry.PayloadCommitment
        Payload = entry.Payload |> Option.map Array.copy
        PayloadNonce = entry.PayloadNonce |> Option.map Array.copy
    }

type private Reader(get: unit -> State, observed, read: unit -> unit) =
    interface IReadTx with
        member _.ObservedAt = observed

        member _.Authority
            ({
                 AuthorityQuery.Principal = principal
                 CaseId = caseId
             })
            =
            read ()
            let state = get ()

            let actor =
                state.Actors
                |> Map.values
                |> Seq.tryFind (fun actor -> actor.Principal = principal)

            let view =
                actor
                |> Option.map (fun actor ->
                    {
                        ActorAuthority.Actor = actor
                        Grants =
                            state.Grants
                            |> Seq.choose (fun (id, role, scope) ->
                                if id = actor.ActorId && (scope.IsNone || scope = caseId) then
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
                    })
                |> AuthorityView.ofActor

            let assigned =
                actor
                |> Option.exists (fun actor ->
                    state.Grants
                    |> Seq.exists (fun (id, role, scope) ->
                        id = actor.ActorId
                        && scope.IsSome
                        && List.contains role (Authority.readRoles principal)
                        && not (
                            state.Grants
                            |> Seq.exists (fun (badId, badRole, badScope) ->
                                badId = id
                                && badScope = scope
                                && List.contains badRole (Authority.invalidRoles principal))
                        )))

            Task.FromResult
                { view with
                    HasAssignedCases = caseId.IsNone && assigned
                }

        member _.FindCase id =
            read ()
            Task.FromResult(Map.tryFind id (get ()).Cases)

        member _.FindEntry id =
            read ()
            Task.FromResult(Map.tryFind id (get ()).Entries |> Option.map copyEntry)

        member _.FindActor id =
            read ()
            Task.FromResult(Map.tryFind id (get ()).Actors)

        member _.ActorCount() =
            read ()
            Task.FromResult((get ()).Actors.Count)

        member _.EnabledHumanOwners() =
            read ()
            let state = get ()

            Task.FromResult(
                state.Actors
                |> Map.values
                |> Seq.filter (fun actor ->
                    actor.Enabled
                    && PrincipalKey.isHuman actor.Principal
                    && Set.contains (actor.ActorId, Role.Owner, None) state.Grants)
                |> Seq.length
            )

        member _.CasePage
            ({
                 CasePageQuery.Visibility = visibility
                 ActorId = actor
                 ReadRoles = roles
                 ForbiddenRoles = forbidden
                 AfterReference = after
                 Limit = limit
             })
            =
            read ()
            let state = get ()

            let rows =
                state.Cases
                |> Map.values
                |> Seq.filter (fun record ->
                    let key = CaseReference.identity record.Snapshot.Reference

                    (after
                     |> Option.forall (fun after -> StringComparer.Ordinal.Compare(key, after) > 0))
                    && (visibility = CaseVisibility.Every
                        || (state.Grants
                            |> Seq.exists (fun (id, role, scope) ->
                                id = actor
                                && scope = Some record.CaseId
                                && List.contains role roles)
                            && not (
                                state.Grants
                                |> Seq.exists (fun (id, role, scope) ->
                                    id = actor
                                    && scope = Some record.CaseId
                                    && List.contains role forbidden)
                            ))))
                |> Seq.sortWith (fun a b ->
                    StringComparer.Ordinal.Compare(
                        CaseReference.identity a.Snapshot.Reference,
                        CaseReference.identity b.Snapshot.Reference
                    ))
                |> Seq.toList

            Task.FromResult(
                page limit (fun record -> CaseReference.identity record.Snapshot.Reference) rows
            )

        member _.HistoryPage
            ({
                 HistoryPageQuery.CaseId = id
                 AfterRevision = after
                 Limit = limit
             })
            =
            read ()

            let rows =
                (get ()).Entries
                |> Map.values
                |> Seq.choose (fun entry ->
                    match entry.Subject with
                    | EntryTarget.Case(caseId, revision, _, _) when caseId = id && revision > after ->
                        Some(revision, copyEntry entry)
                    | _ -> None)
                |> Seq.sortBy fst
                |> Seq.map snd
                |> Seq.toList

            let revision (entry: StoredEntry) =
                match entry.Subject with
                | EntryTarget.Case(_, revision, _, _) -> revision
                | _ -> failwith "Invalid history fixture"

            Task.FromResult(page limit revision rows)

        member _.ActorPage
            ({
                 ActorPageQuery.AfterActorId = after
                 Limit = limit
             })
            =
            read ()

            let rows =
                (get ()).Actors
                |> Map.values
                |> Seq.filter (fun actor ->
                    after |> Option.forall (fun after -> compareId actor.ActorId after > 0))
                |> Seq.sortWith (fun a b -> compareId a.ActorId b.ActorId)
                |> Seq.toList

            Task.FromResult(page limit (fun actor -> actor.ActorId) rows)

        member _.GrantPage
            ({
                 GrantPageQuery.ActorId = actor
                 AfterGrant = after
                 Limit = limit
             })
            =
            read ()
            let state = get ()

            let compareScope a b =
                match a, b with
                | None, None -> 0
                | None, Some _ -> 1
                | Some _, None -> -1
                | Some a, Some b -> compareId a b

            let rows =
                state.Grants
                |> Seq.choose (fun (id, role, scope) ->
                    if id <> actor then
                        None
                    else
                        let position = { Role = role; CaseId = scope }

                        let included =
                            after
                            |> Option.forall (fun after ->
                                let ordering =
                                    StringComparer.Ordinal.Compare(
                                        Role.token role,
                                        Role.token after.Role
                                    )

                                ordering > 0
                                || (ordering = 0 && compareScope scope after.CaseId > 0))

                        if not included then
                            None
                        else
                            let target =
                                match scope with
                                | None -> GrantTarget.Installation
                                | Some id ->
                                    GrantTarget.CaseReference(
                                        CaseReference.format state.Cases[id].Snapshot.Reference
                                    )

                            Some(
                                {
                                    AuthoredGrant.Role = role
                                    Scope = target
                                },
                                position
                            ))
                |> Seq.sortWith (fun (_, a) (_, b) ->
                    let role = StringComparer.Ordinal.Compare(Role.token a.Role, Role.token b.Role)

                    if role = 0 then compareScope a.CaseId b.CaseId else role)
                |> Seq.toList

            let visible = page limit snd rows

            Task.FromResult
                {
                    Items = visible.Items |> List.map fst
                    Next = visible.Next
                }

type private Writer(get, set, observed, head, nonce, read) =
    inherit Reader(get, observed, read)

    interface IWriteTx with
        member _.Head = head
        member _.Nonce = nonce

        member _.IsFenced
            ({
                 FenceKey.ActorId = actor
                 OperationId = operation
             })
            =
            read ()
            Task.FromResult(Set.contains (actor, operation) (get ()).Fences)

        member _.Append draft =
            let state = get ()

            if
                draft.Sequence <> state.Head.Sequence + 1L
                || draft.PreviousHash <> EntryHash.toBytes state.Head.EntryHash
                || Map.containsKey draft.OperationId state.Entries
            then
                invalidOp "An append must extend the owned head with a fresh operation."

            set
                { state with
                    Head =
                        {
                            Sequence = draft.Sequence
                            EntryHash = EntryHash.ofBytes draft.EntryHash |> Option.get
                        }
                    Entries =
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
                | CaseProjection.Insert(id, snapshot)
                | CaseProjection.Update(id, snapshot) ->
                    Map.add id { CaseId = id; Snapshot = snapshot } state.Cases
                | CaseProjection.Erase(id, revision) ->
                    Map.add
                        id
                        {
                            CaseId = id
                            Snapshot =
                                { state.Cases[id].Snapshot with
                                    Revision = revision
                                    State = CaseState.Erased
                                }
                        }
                        state.Cases

            let entries =
                match projection with
                | CaseProjection.Erase(id, _) ->
                    state.Entries
                    |> Map.map (fun _ entry ->
                        match entry.Subject with
                        | EntryTarget.Case(caseId, _, _, _) when caseId = id ->
                            { entry with
                                Payload = None
                                PayloadNonce = None
                            }
                        | _ -> entry)
                | _ -> state.Entries

            set
                { state with
                    Cases = cases
                    Entries = entries
                }

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
                    Fences = Set.add (actor, operation) (get ()).Fences
                }

            Task.FromResult(())

/// One transactional double for Application tests; refusals and replay discard pending state.
type Store(observed: DateTimeOffset) =
    let gate = new SemaphoreSlim(1, 1)
    let events = ResizeArray<string>()

    let mutable state =
        {
            Head =
                {
                    Sequence = 0L
                    EntryHash = EntryHash.zero
                }
            Entries = Map.empty
            Cases = Map.empty
            Actors = Map.empty
            Grants = Set.empty
            Fences = Set.empty
        }

    member val ReadFailure: StoreFailure option = None with get, set
    member val WriteFailure: StoreFailure option = None with get, set
    member val Calls = 0 with get, set
    member _.Events = List.ofSeq events

    member _.SeedActor (actor: Actor) grants =
        state <-
            { state with
                Actors = Map.add actor.ActorId actor state.Actors
                Grants =
                    grants
                    |> List.fold
                        (fun all (role, scope) -> Set.add (actor.ActorId, role, scope) all)
                        state.Grants
            }

    interface IStore with
        member this.Read
            ({
                 ReadOperation.Work = work
                 CancellationToken = token
             })
            =
            task {
                this.Calls <- this.Calls + 1

                match this.ReadFailure with
                | Some failure -> return Error failure
                | None ->
                    do! gate.WaitAsync token

                    try
                        let snapshot = state

                        try
                            let! result =
                                work (
                                    Reader(
                                        (fun () -> snapshot),
                                        observed,
                                        (fun () -> events.Add "read")
                                    )
                                    :> IReadTx
                                )

                            return Ok result
                        with :? OperationCanceledException ->
                            return Error StoreFailure.Cancelled
                    finally
                        gate.Release() |> ignore
            }

        member this.Write
            ({
                 WriteOperation.Work = work
                 CancellationToken = token
             })
            =
            task {
                this.Calls <- this.Calls + 1

                match this.WriteFailure with
                | Some failure -> return Error failure
                | None ->
                    do! gate.WaitAsync token

                    try
                        events.Clear()
                        events.Add "lock"
                        let mutable pending = state

                        let tx =
                            Writer(
                                (fun () -> pending),
                                (fun value -> pending <- value),
                                observed,
                                state.Head,
                                RandomNumberGenerator.GetBytes 32,
                                (fun () -> events.Add "read")
                            )
                            :> IWriteTx

                        try
                            let! verdict = work tx

                            match verdict with
                            | Verdict.Proceed _ -> state <- pending
                            | Verdict.Replay _
                            | Verdict.Refuse _
                            | Verdict.Fail _ -> ()

                            return Ok verdict
                        with
                        | :? OperationCanceledException -> return Error StoreFailure.Cancelled
                        | _ -> return Error StoreFailure.Internal
                    finally
                        gate.Release() |> ignore
            }

    interface IDisposable with
        member _.Dispose() = gate.Dispose()
