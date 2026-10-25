module ClaimCore.Tests.MemoryLedgerStore

open ClaimCore.Contract
open ClaimCore.Contract.Protocol

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

let private storedActor (actor: ActorAuthority) : Actor =
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
    let role =
        String.CompareOrdinal(RoleTokens.token left.Role, RoleTokens.token right.Role)

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

    let recovery =
        System.Collections.Generic.Dictionary<
            Guid,
            Guid * Guid option * ExecutionEnvelope * RecoveryItem
         >()

    let mutable observedAt = instant
    let mutable nextFailure: StoreFailure option = None
    let mutable unknownLands = true
    let mutable afterCommit = ignore
    let mutable calls = 0
    let mutable forcedHead: LedgerHead option = None
    let actor principal = Map.tryFind principal state.Actors

    let authority principal ids =
        let all = actor principal |> AuthorityView.ofActor
        let cases = all.Cases |> Map.filter (fun id _ -> List.contains id ids)

        let readable roles =
            roles
            |> List.exists (fun role ->
                ActorAuthorization.readRoles principal |> List.contains role)

        let valid roles =
            PrincipalKey.isHuman principal
            || roles |> List.forall (ActorAuthorization.humanOnlyRole >> not)

        { all with
            Cases = cases
            HasAssignedCases =
                all.Cases |> Map.exists (fun _ roles -> readable roles && valid roles)
        }

    let operationCases entry =
        match entry |> Option.map (fun (entry: StoredEntry) -> entry.Scope) with
        | Some(StoredScope.Case(id, _, _, _)) -> [ id ]
        | _ -> []

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

    let caseRead principal id reference : CaseReadView =
        let existing = id |> Option.bind operation

        {
            ObservedAt = observedAt
            Actor =
                authority
                    principal
                    ((case reference |> Option.map _.CaseId |> Option.toList)
                     @ operationCases existing)
            ExistingOperation = existing
            Case = case reference
        }

    let authorityRead principal id target reference : AuthorityReadView =
        let existing = operation id
        let record = reference |> Option.bind case
        let ids = record |> Option.map _.CaseId |> Option.toList

        {
            ObservedAt = observedAt
            Actor = authority principal (ids @ operationCases existing)
            ExistingOperation = existing
            Target = authority target ids
            TargetCase = record
            EnabledHumanOwners = owners ()
            ActorCount = state.Actors.Count
        }

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
            | _ -> return Error StoreFailure.Internal
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

    let recoveryMetadata id target (envelope: ExecutionEnvelope) =
        let subject =
            match envelope.Input with
            | OperationInput.Case request ->
                RecoverySubject.Case
                    {
                        CaseReference = request.CaseReference
                        ExpectedRevision = request.ExpectedRevision
                        Act = CaseActs.kind request.Act
                    }
            | OperationInput.Authority request ->
                let reference =
                    match request.Act with
                    | AuthorityAct.SetGrant {
                                                Scope = GrantTarget.CaseReference reference
                                            } -> Some reference
                    | _ -> None

                RecoverySubject.Authority
                    {
                        TargetActorId = Option.get target
                        CaseReference = reference
                        Act = RecoveryAuthorityKinds.fromAct request.Act
                    }

        {
            RecoveryId = id
            OperationId = OperationInput.operationId envelope.Input
            Subject = subject
            CreatedAt = observedAt
            ExpiresAt = observedAt.AddDays(float ServiceLimits.RecoveryRetentionDays)
            Offers = []
        }

    let recoveryCleanup principal (envelope: ExecutionEnvelope) =
        let expired =
            recovery
            |> Seq.filter (fun pair ->
                let _, _, _, item = pair.Value in item.ExpiresAt <= observedAt)
            |> Seq.sortBy (fun pair ->
                let owner, _, _, item = pair.Value

                let matches =
                    actor principal
                    |> Option.exists (fun value ->
                        value.ActorId = owner
                        && item.OperationId = OperationInput.operationId envelope.Input)

                (if matches then 0 else 1), item.ExpiresAt, pair.Key)
            |> Seq.map _.Key
            |> Seq.truncate ServiceLimits.RecoveryCleanupBatch
            |> Seq.toList

        for key in expired do
            recovery.Remove key |> ignore


    let replayLocator principal (envelope: ExecutionEnvelope) result =
        match result, actor principal with
        | ExecuteResult.AlreadyAccepted _, Some actor ->
            recovery
            |> Seq.tryPick (fun pair ->
                let owner, _, body, item = pair.Value

                if
                    owner = actor.ActorId
                    && body.Input = envelope.Input
                    && item.ExpiresAt > observedAt
                then
                    Some pair.Key
                else
                    None)
        | _ -> None

    let recoveryStage principal (envelope: ExecutionEnvelope) admission =
        match admission with
        | RecoveryAdmission.Terminal result ->
            Ok(RecoveryStaged.Terminal(result, replayLocator principal envelope result))
        | RecoveryAdmission.Ready(actor, caseId, target) ->
            recoveryCleanup principal envelope

            let existing =
                recovery
                |> Seq.tryFind (fun pair ->
                    let owner, _, _, item = pair.Value in
                    owner = actor && item.OperationId = OperationInput.operationId envelope.Input)

            match existing with
            | Some pair ->
                let _, _, body, _ = pair.Value

                if body.Input = envelope.Input then
                    Ok(RecoveryStaged.Stored(pair.Key, actor, body))
                else
                    Ok(
                        RecoveryStaged.Terminal(
                            ExecuteResult.Rejected Refusal.IdempotencyConflict,
                            None
                        )
                    )
            | None ->
                let owned =
                    recovery.Values
                    |> Seq.filter (fun (owner, _, _, _) -> owner = actor)
                    |> Seq.length

                if
                    owned >= ServiceLimits.RecoveryPerActor
                    || recovery.Count >= ServiceLimits.RecoveryInstallation
                then
                    Ok(
                        RecoveryStaged.Terminal(
                            ExecuteResult.Failed CoreFault.StoreUnavailable,
                            None
                        )
                    )
                else
                    let id = Guid.NewGuid()

                    let item = recoveryMetadata id target envelope

                    recovery.Add(id, (actor, caseId, envelope, item))
                    Ok(RecoveryStaged.Stored(id, actor, envelope))

    let recoveryStore =
        { new IRecoveryStore with
            member _.StageCase(principal, envelope, decide, ct) =
                locked ct (fun () ->
                    match envelope.Input with
                    | OperationInput.Case request ->
                        recoveryStage
                            principal
                            envelope
                            (decide (
                                caseRead principal (Some request.OperationId) request.CaseReference
                            ))
                    | _ -> Error StoreFailure.Internal)

            member _.StageAuthority(principal, request, envelope, decide, ct) =
                locked ct (fun () ->
                    recoveryStage
                        principal
                        envelope
                        (decide (
                            authorityRead
                                principal
                                request.OperationId
                                request.Target
                                (AuthorityRequests.targetCase request)
                        )))

            member _.Pending(actor, after, limit, ct) =
                locked ct (fun () ->
                    Ok(
                        recovery.Values
                        |> Seq.choose (fun (owner, _, _, item) ->
                            if
                                owner = actor
                                && item.ExpiresAt > observedAt
                                && (after |> Option.forall (fun id -> item.RecoveryId > id))
                            then
                                Some item
                            else
                                None)
                        |> Seq.sortBy _.RecoveryId
                        |> Seq.truncate limit
                        |> Seq.toList
                    ))

            member _.Load(actor, request, ct) =
                locked ct (fun () ->
                    let body =
                        match recovery.TryGetValue request.RecoveryId with
                        | true, (owner, _, body, item) when
                            owner = actor
                            && item.OperationId = request.OperationId
                            && item.ExpiresAt > observedAt
                            ->
                            Some body
                        | _ -> None

                    Ok body)

            member _.Forget(actor, id, ct) =
                locked ct (fun () ->
                    match recovery.TryGetValue id with
                    | true, (owner, _, _, _) when owner = actor -> recovery.Remove id |> ignore
                    | _ -> ()

                    Ok())

            member _.Retained(actor, id, ct) =
                locked ct (fun () ->
                    let retained =
                        match recovery.TryGetValue id with
                        | true, (owner, _, _, item) -> owner = actor && item.ExpiresAt > observedAt
                        | _ -> false

                    Ok retained)
        }


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

    member _.ReplaceCaseSnapshot(reference, snapshot) =
        let key = CaseReference.key reference
        let original = state.Cases[key]

        state <-
            { state with
                Cases = state.Cases |> Map.add key { original with Snapshot = snapshot }
            }

    member _.DecisionCalls = calls
    member _.Entries = state.Entries |> List.map copyEntry
    member _.Cases = state.Cases |> Map.values |> Seq.toList
    member _.Actors = state.Actors |> Map.values |> Seq.toList

    interface IDisposable with
        member _.Dispose() = gate.Dispose()

    interface ILedgerStore with
        member _.Recovery = recoveryStore

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
                            PayloadNonce = nonce ()
                            Read = caseRead principal (Some id) reference
                        }

                    let decision = decide view
                    let result = write decision applyCase failure

                    match decision with
                    | LedgerDecision.Append(_, CaseProjection.Erase(id, _), _) when
                        failure.IsNone || unknownLands
                        ->
                        let keys =
                            recovery
                            |> Seq.filter (fun pair ->
                                let _, caseId, _, _ = pair.Value in caseId = Some id)
                            |> Seq.map _.Key
                            |> Seq.toList

                        for key in keys do
                            recovery.Remove key |> ignore
                    | _ -> ()

                    result)

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
                            PayloadNonce = nonce ()
                            Read = authorityRead principal id target reference
                        }

                    write (decide view) applyAuthority failure)

        member _.ReadAuthorityTarget(principal, id, target, reference, ct) =
            locked ct (fun () -> Ok(authorityRead principal id target reference))

        member _.ReadCase(principal, id, reference, ct) =
            locked ct (fun () -> Ok(caseRead principal id reference))

        member _.ReadActor(principal, ct) =
            locked ct (fun () ->
                Ok
                    {
                        ObservedAt = observedAt
                        Actor = authority principal []
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
                        Actor = authority principal (operationCases entry)
                        Entry = entry
                        Case = current
                    })

        member _.ReadHistory(principal, reference, select, limit, ct) =
            locked ct (fun () ->
                let view: HistoryReadView =
                    {
                        ObservedAt = observedAt
                        Actor =
                            authority
                                principal
                                (case reference |> Option.map _.CaseId |> Option.toList)
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

        member _.ListCases(principal, readRoles, select, limit, ct) =
            locked ct (fun () ->
                let view: ActorReadView =
                    {
                        ObservedAt = observedAt
                        Actor = authority principal []
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
                             | CaseVisibility.Assigned ->
                                 let scoped = authority principal [ record.CaseId ]

                                 ActorAuthorization.validActor principal scoped
                                 && (scoped.Cases
                                     |> Map.tryFind record.CaseId
                                     |> Option.defaultValue []
                                     |> List.exists (fun role -> List.contains role readRoles)))
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
                        Actor = authority principal []
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
                                Items = visible |> List.map storedActor
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
                        Actor = authority principal []
                        Target = target |> Option.map storedActor
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
                                Target = storedActor target
                                Items = visible |> List.map (reviewGrant state)
                                NextAfterGrant =
                                    if rows.Length > limit then
                                        visible |> List.tryLast |> Option.map grantPosition
                                    else
                                        None
                            }
                    ))
