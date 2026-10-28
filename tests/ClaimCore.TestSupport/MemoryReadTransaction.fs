module internal ClaimCore.TestSupport.MemoryReadTransaction

open ClaimCore.RecordFormat
open System
open System.Threading
open System.Threading.Tasks
open System.Security.Cryptography
open ClaimCore.Application
open ClaimCore.Protocol
open ClaimCore.Domain

open ClaimCore.TestSupport.MemoryStoreState

type internal Reader(get: unit -> State, installationId, observed, read: unit -> unit) =
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
            Task.FromResult(Map.tryFind id (get ()).Cases |> Option.map (readCase installationId))

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

        member _.CasePage(query: CasePageQuery) =
            read ()
            let state = get ()

            let reference (record: StoredCase) =
                CaseReference.identity record.Snapshot.Reference

            let compareRows (left: StoredCase) (right: StoredCase) =
                let time =
                    if query.Filter.Order = CaseListOrder.Recent then
                        compare right.Index.ChangedAt left.Index.ChangedAt
                    else
                        0

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

            let visible (record: StoredCase) =
                (query.Visibility = CaseVisibility.Every
                 || (state.Grants
                     |> Seq.exists (fun (id, role, scope) ->
                         id = query.ActorId
                         && scope = Some record.CaseId
                         && List.contains role query.ReadRoles)))
                && not (
                    state.Grants
                    |> Seq.exists (fun (id, role, scope) ->
                        id = query.ActorId
                        && scope = Some record.CaseId
                        && List.contains role query.ForbiddenRoles)
                )

            let rows =
                state.Cases
                |> Map.values
                |> Seq.filter (fun record ->
                    visible record
                    && after record
                    && CaseListFilter.matches
                        query.Filter
                        (CaseState.lifecycle record.Snapshot.State)
                        record.Index.Status
                        record.Index.Progress
                        record.Index.SearchText)
                |> Seq.sortWith compareRows
                |> Seq.map (readCase installationId)
                |> Seq.toList

            Task.FromResult(
                page
                    query.Limit
                    (fun record ->
                        CaseListPosition.create
                            query.Filter.Order
                            record.Index.ChangedAt
                            (CaseReference.format record.Snapshot.Reference))
                    rows
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
