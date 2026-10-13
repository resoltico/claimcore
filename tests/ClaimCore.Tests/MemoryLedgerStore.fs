module ClaimCore.Tests.MemoryLedgerStore

open System
open System.Threading
open System.Threading.Tasks

exception private UnknownCommitFault

open ClaimCore.Domain
open ClaimCore.Application

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
        Payload = entry.Payload |> Option.map Array.copy
        RequestSha256 = entry.RequestSha256 |> Option.map Array.copy
    }

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

let private applyAuthority projection state =
    match projection with
    | AuthorityProjection.RegisterActor(id, principal) -> registerActor id principal [] state
    | AuthorityProjection.ProvisionOwner(id, principal) ->
        registerActor
            id
            principal
            [
                {
                    Role = Role.Owner
                    Scope = GrantScope.Installation
                }
            ]
            state
    | AuthorityProjection.SetEnabled(id, enabled) ->
        updateActor id (fun value -> { value with Enabled = enabled }) state
    | AuthorityProjection.AddGrant(id, role, caseId) ->
        let grant =
            {
                Role = role
                Scope =
                    caseId
                    |> Option.map GrantScope.Case
                    |> Option.defaultValue GrantScope.Installation
            }

        updateActor
            id
            (fun value ->
                { value with
                    Grants = grant :: value.Grants |> List.distinct
                })
            state
    | AuthorityProjection.RemoveGrant(id, role, caseId) ->
        let grant =
            {
                Role = role
                Scope =
                    caseId
                    |> Option.map GrantScope.Case
                    |> Option.defaultValue GrantScope.Installation
            }

        updateActor
            id
            (fun value ->
                { value with
                    Grants = value.Grants |> List.filter ((<>) grant)
                })
            state

let private eraseCase id revision state =
    let original =
        state.Cases |> Map.values |> Seq.find (fun value -> value.CaseId = id)

    { state with
        Cases =
            state.Cases
            |> Map.add
                original.Reference
                { original with
                    Revision = revision
                    State = StoredCaseState.Erased
                }
        Entries =
            state.Entries
            |> List.map (fun entry ->
                match entry.Scope with
                | StoredScope.Case(caseId, _, _, _) when caseId = id ->
                    { entry with
                        Payload = None
                        RequestSha256 = None
                    }
                | _ -> entry)
    }

let private applyCase projection state =
    match projection with
    | CaseProjection.Insert(id, view) ->
        if Map.containsKey view.Fields.CaseReference state.Cases then
            invalidOp "Duplicate case."

        { state with
            Cases =
                state.Cases
                |> Map.add
                    view.Fields.CaseReference
                    {
                        CaseId = id
                        Reference = view.Fields.CaseReference
                        Revision = view.Version
                        State = StoredCaseState.Live(view, CaseDisposition.Active)
                    }
        }
    | CaseProjection.Update(id, view, disposition) ->
        let original = state.Cases[view.Fields.CaseReference]

        if original.CaseId <> id then
            invalidOp "Wrong case projection."

        { state with
            Cases =
                state.Cases
                |> Map.add
                    original.Reference
                    { original with
                        Revision = view.Version
                        State = StoredCaseState.Live(view, disposition)
                    }
        }
    | CaseProjection.Erase(id, revision) -> eraseCase id revision state

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
    let actor key = Map.tryFind key state.Actors
    let case reference = Map.tryFind reference state.Cases

    let operation id =
        state.Entries
        |> List.tryFind (fun entry -> entry.OperationId = id)
        |> Option.map copyEntry

    let head () : LedgerHead =
        match state.Entries |> List.tryLast with
        | Some entry ->
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
            entry.Sequence <> head.Sequence + 1L
            || entry.PreviousHash <> head.EntryHash
            || operation entry.OperationId |> Option.isSome
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
        |> Seq.filter (fun value ->
            value.Enabled
            && PrincipalKey.isHuman value.Principal
            && List.contains
                {
                    Role = Role.Owner
                    Scope = GrantScope.Installation
                }
                value.Grants)
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

    member _.Instant
        with get () = observedAt
        and set value = observedAt <- value

    member _.NextFailure
        with set value = nextFailure <- Some value

    member _.UnknownCommitLands
        with set value = unknownLands <- value

    member _.AfterCommit
        with set value = afterCommit <- value

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

                    match decide view with
                    | CaseDecision.Refuse error -> Ok(CaseWriteResult.Refused error)
                    | CaseDecision.Fail fault -> Ok(CaseWriteResult.Failed fault)
                    | CaseDecision.Replay entry -> Ok(CaseWriteResult.Replayed entry)
                    | CaseDecision.Append(entry, projection) ->
                        if failure.IsNone || unknownLands then
                            commit entry (applyCase projection)

                        if failure.IsSome then
                            Error StoreFailure.CommitOutcomeUnknown
                        else
                            Ok(CaseWriteResult.Appended entry))

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

                    match decide view with
                    | AuthorityDecision.Refuse error -> Ok(AuthorityWriteResult.Refused error)
                    | AuthorityDecision.Fail fault -> Ok(AuthorityWriteResult.Failed fault)
                    | AuthorityDecision.Replay entry -> Ok(AuthorityWriteResult.Replayed entry)
                    | AuthorityDecision.Append(entry, projection) ->
                        if failure.IsNone || unknownLands then
                            commit entry (applyAuthority projection)

                        if failure.IsSome then
                            Error StoreFailure.CommitOutcomeUnknown
                        else
                            Ok(AuthorityWriteResult.Appended entry))

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

                let record =
                    entry
                    |> Option.bind (fun value ->
                        match value.Scope with
                        | StoredScope.Case(id, _, _, _) ->
                            state.Cases
                            |> Map.values
                            |> Seq.tryFind (fun value -> value.CaseId = id)
                        | _ -> None)

                Ok(actor principal, entry, record))

        member _.ReadHistory(principal, reference, after, limit, ct) =
            locked ct (fun () ->
                let record = case reference

                let rows =
                    state.Entries
                    |> List.filter (fun entry ->
                        match entry.Scope, record with
                        | StoredScope.Case(id, revision, _, _), Some case ->
                            id = case.CaseId && revision > after
                        | _ -> false)
                    |> List.sortBy (fun entry ->
                        match entry.Scope with
                        | StoredScope.Case(_, revision, _, _) -> revision
                        | _ -> 0L)
                    |> List.truncate (limit + 1)
                    |> List.map copyEntry

                let visible = rows |> List.truncate limit

                let next =
                    if rows.Length > limit then
                        visible
                        |> List.tryLast
                        |> Option.map (fun entry ->
                            match entry.Scope with
                            | StoredScope.Case(_, revision, _, _) -> revision
                            | _ -> invalidOp "Case history required.")
                    else
                        None

                Ok
                    {
                        Actor = actor principal
                        Case = record
                        Entries = visible
                        NextAfterRevision = next
                    })

        member _.ListCases(principal, visibility, after, limit, ct) =
            locked ct (fun () ->
                match actor principal |> Option.bind visibility with
                | None -> Ok None
                | Some visibility ->
                    let rows =
                        state.Cases
                        |> Map.values
                        |> Seq.filter (fun value ->
                            (match visibility with
                             | CaseVisibility.Every -> true
                             | CaseVisibility.Only ids -> List.contains value.CaseId ids)
                            && (after
                                |> Option.forall (fun position ->
                                    String.CompareOrdinal(value.Reference, position) > 0)))
                        |> Seq.sortWith (fun a b ->
                            String.CompareOrdinal(a.Reference, b.Reference))
                        |> Seq.truncate (limit + 1)
                        |> Seq.toList

                    let items = rows |> List.truncate limit

                    let next =
                        if rows.Length > limit then
                            items |> List.tryLast |> Option.map _.Reference
                        else
                            None

                    Ok(
                        Some
                            {
                                Actor = actor principal
                                Items = items
                                NextAfterReference = next
                            }
                    ))
