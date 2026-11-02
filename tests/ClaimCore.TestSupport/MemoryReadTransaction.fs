module internal ClaimCore.TestSupport.MemoryReadTransaction

open ClaimCore.RecordFormat
open System
open System.Threading
open System.Threading.Tasks
open System.Security.Cryptography
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Application.Contracts

open ClaimCore.TestSupport.MemoryStoreState

let private selected (state: State) (selection: CaseSelection) (record: StoredCase) =
    let scopedRead =
        state.Grants
        |> Seq.exists (fun (id, role, scope) ->
            id = selection.ActorId
            && scope = Some record.CaseId
            && List.contains role selection.ReadRoles)

    let forbidden =
        state.Grants
        |> Seq.exists (fun (id, role, scope) ->
            id = selection.ActorId
            && scope = Some record.CaseId
            && List.contains role selection.ForbiddenRoles)

    (selection.Visibility = CaseVisibility.Every || scopedRead)
    && (selection.Filter.Assignment
        |> Option.forall (function
            | CaseAssignment.Mine -> record.Index.Responsible = Some selection.ActorId
            | CaseAssignment.Unassigned ->
                record.Index.Responsible.IsNone
                && CaseState.lifecycle record.Snapshot.State <> Lifecycle.Erased))
    && (selection.Filter.Due
        |> Option.forall (fun due ->
            CaseDue.at
                selection.DateBasis.BusinessDate
                (CaseState.lifecycle record.Snapshot.State)
                record.Index.Status
                {
                    Responsible = record.Index.Responsible
                    FollowUp = record.Index.FollowUp
                }
                =
                due))
    && not forbidden
    && CaseListFilter.matches
        selection.Filter
        (CaseState.lifecycle record.Snapshot.State)
        record.Index.Status
        record.Index.Progress
        record.Index.SearchText

let private total money =
    money
    |> Option.map (fun value ->
        {
            CurrencyTotal.Currency = Money.currencyValue value
            Amount =
                DecimalTotal.parse (Money.format value)
                |> Result.defaultWith (string >> failwith)
        })

let private compareDates (left: CalendarDate option) right =
    match left, right with
    | None, None -> 0
    | None, Some _ -> 1
    | Some _, None -> -1
    | Some left, Some right -> compare left right

let private dateAfter (found: CalendarDate option) boundary referenceAfter =
    match found, boundary with
    | None, Some _ -> true
    | Some _, None -> false
    | None, None -> referenceAfter
    | Some found, Some boundary -> found > boundary || (found = boundary && referenceAfter)

type internal Reader
    (
        get: unit -> State,
        _installationId,
        observed,
        read: unit -> unit,
        ?factRead: AuthorityFact -> unit
    ) =
    let factRead = defaultArg factRead ignore

    interface IReadTx with
        member _.ObservedAt = observed
        member _.Maintenance() = Task.FromResult((get ()).Maintenance)

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
                    HasScopedCases = caseId.IsNone && assigned
                }

        member _.FindCase id =
            read ()
            Task.FromResult(Map.tryFind id (get ()).Cases |> Option.map (readCase (get ()).Entries))

        member _.FindCaseByReference reference =
            read ()

            Task.FromResult(
                (get ()).Cases
                |> Map.values
                |> Seq.tryFind (fun record ->
                    CaseReference.identity record.Snapshot.Reference = CaseReference.key reference)
                |> Option.map (readCase (get ()).Entries)
            )

        member _.FindEntry id =
            read ()
            Task.FromResult(Map.tryFind id (get ()).Entries |> Option.map copyEntry)

        member _.NonAcceptance(key: FenceKey) =
            read ()

            let controls =
                (get ()).Controls
                |> Map.values
                |> Seq.filter (fun entry ->
                    entry.Entry.ActorId = key.ActorId && entry.Entry.OperationId = key.OperationId)
                |> Seq.toList

            let control =
                match controls with
                | [] -> None
                | [ entry ] ->
                    Some
                        { entry with
                            Entry = copyEntry entry.Entry
                        }
                | _ -> raise CorruptProjection

            Task.FromResult
                {
                    NonAcceptanceEvidence.Control = control
                    ProjectedAt = Map.tryFind (key.ActorId, key.OperationId) (get ()).Fences
                }

        member _.AssigneePage query =
            read ()
            let state = get ()

            let permitted roles id =
                state.Grants
                |> Seq.exists (fun (actor, role, caseId) ->
                    actor = id
                    && (caseId.IsNone || caseId = Some query.CaseId)
                    && List.contains role roles)

            let rows =
                state.Actors
                |> Map.values
                |> Seq.filter (fun actor ->
                    actor.Enabled
                    && PrincipalKey.isHuman actor.Principal
                    && (query.AfterActorId |> Option.forall (fun after -> actor.ActorId > after))
                    && permitted query.ReadRoles actor.ActorId
                    && permitted query.WorkRoles actor.ActorId)
                |> Seq.sortBy _.ActorId
                |> Seq.truncate (query.Limit + 1)
                |> Seq.map (fun actor ->
                    let _, _, subject = PrincipalKey.parts actor.Principal

                    {
                        AccountSubject.ActorId = actor.ActorId
                        Subject = subject
                    })
                |> Seq.toList

            Task.FromResult(page query.Limit _.ActorId rows)

        member _.FindActor id =
            read ()
            Task.FromResult(Map.tryFind id (get ()).Actors)

        member _.ActorCount() =
            read ()
            factRead AuthorityFact.ActorCount
            Task.FromResult((get ()).Actors.Count)

        member _.EnabledHumanOwners() =
            read ()
            factRead AuthorityFact.EnabledHumanOwners
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

        member _.CasePage(query: CasePageQuery) =
            read ()
            let state = get ()

            let reference (record: StoredCase) =
                CaseReference.identity record.Snapshot.Reference

            let compareRows (left: StoredCase) (right: StoredCase) =
                let time =
                    match query.Filter.Order with
                    | CaseListOrder.Reference -> 0
                    | CaseListOrder.Recent -> compare right.Index.ChangedAt left.Index.ChangedAt
                    | CaseListOrder.Notified ->
                        compareDates left.Index.NotifiedOn right.Index.NotifiedOn
                    | CaseListOrder.FollowUp ->
                        compareDates left.Index.FollowUp right.Index.FollowUp

                if time <> 0 then
                    time
                else
                    StringComparer.Ordinal.Compare(reference left, reference right)

            let after (record: StoredCase) =
                match query.AfterPosition with
                | None -> true
                | Some(CaseListPosition.Reference value) ->
                    StringComparer.Ordinal.Compare(reference record, value) > 0
                | Some(CaseListPosition.Recent(time, value)) ->
                    record.Index.ChangedAt < time
                    || (record.Index.ChangedAt = time
                        && StringComparer.Ordinal.Compare(reference record, value) > 0)
                | Some(CaseListPosition.Notified(date, value)) ->
                    dateAfter
                        record.Index.NotifiedOn
                        date
                        (StringComparer.Ordinal.Compare(reference record, value) > 0)
                | Some(CaseListPosition.FollowUp(date, value)) ->
                    dateAfter
                        record.Index.FollowUp
                        date
                        (StringComparer.Ordinal.Compare(reference record, value) > 0)


            let rows =
                state.Cases
                |> Map.values
                |> Seq.filter (fun record -> selected state query.Selection record && after record)
                |> Seq.sortWith compareRows
                |> Seq.map (readCase (get ()).Entries)
                |> Seq.toList

            Task.FromResult(
                page
                    query.Limit
                    (fun record ->
                        CaseListPosition.create
                            query.Filter.Order
                            record.Index.ChangedAt
                            record.Index.NotifiedOn
                            record.Index.FollowUp
                            (CaseReference.format record.Snapshot.Reference))
                    rows
            )

        member _.CaseSummary selection =
            read ()
            let state = get ()

            let groups =
                state.Cases
                |> Map.values
                |> Seq.filter (selected state selection)
                |> Seq.map (fun record ->
                    {
                        CaseCountGroup.Responsibility =
                            if record.Index.Responsible = Some selection.ActorId then
                                Some CaseAssignment.Mine
                            elif
                                record.Index.Responsible.IsNone
                                && CaseState.lifecycle record.Snapshot.State <> Lifecycle.Erased
                            then
                                Some CaseAssignment.Unassigned
                            else
                                None
                        Due =
                            CaseDue.at
                                selection.DateBasis.BusinessDate
                                (CaseState.lifecycle record.Snapshot.State)
                                record.Index.Status
                                {
                                    Responsible = record.Index.Responsible
                                    FollowUp = record.Index.FollowUp
                                }
                        Lifecycle = CaseState.lifecycle record.Snapshot.State
                        Status = record.Index.Status
                        Progress = record.Index.Progress
                        Count = 1L
                        Claimed = total record.Index.Claimed
                        Payable = total record.Index.Payable
                    })
                |> Seq.toList

            Task.FromResult(
                CaseWorklistSummary.fromGroups selection.DateBasis groups
                |> Result.defaultWith failwith
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
                    | EntryTarget.Case(caseId, revision, _, _) when
                        caseId = id && (after = Revision.zero || revision < after)
                        ->
                        Some(revision, copyEntry entry)
                    | _ -> None)
                |> Seq.sortByDescending fst
                |> Seq.map snd
                |> Seq.toList

            let revision (entry: StoredEntry) =
                match entry.Subject with
                | EntryTarget.Case(_, revision, _, _) -> revision
                | _ -> failwith "Invalid history fixture"

            Task.FromResult
                {
                    Page = page limit revision rows
                    Previous = List.tryItem limit rows
                }

        member _.AuthorityHistoryPage(query: AuthorityHistoryPageQuery) =
            read ()

            let rows =
                (get ()).Entries
                |> Map.values
                |> Seq.filter (fun entry ->
                    match entry.Subject with
                    | EntryTarget.Authority(target, _) ->
                        (query.TargetActorId |> Option.forall ((=) target))
                        && (query.BeforeSequence
                            |> Option.forall (fun before -> entry.Sequence < before))
                    | _ -> false)
                |> Seq.sortByDescending _.Sequence
                |> Seq.map copyEntry
                |> Seq.toList

            Task.FromResult(page query.Limit (fun entry -> entry.Sequence) rows)

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
                    after
                    |> Option.forall (fun after ->
                        compareId ActorId.format actor.ActorId after > 0))
                |> Seq.sortWith (fun a b -> compareId ActorId.format a.ActorId b.ActorId)
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
                | Some a, Some b -> compareId CaseId.format a b

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
                                | None -> GrantScope.Installation
                                | Some id ->
                                    let current = readCase state.Entries state.Cases[id]
                                    GrantScope.Case(id, current.Snapshot.Reference)

                            Some(
                                {
                                    ScopedGrant.Role = role
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
