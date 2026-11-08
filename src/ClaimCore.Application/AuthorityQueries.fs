namespace ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Every read authorizes and decodes its continuation inside one store snapshot.
module internal AuthorityQueries =
    open QueryResults

    let selectActors installation principal (request: ActorListRequest) actor =
        if not (Authority.can principal actor Capability.ReadAuthority Scope.Installation) then
            Error(Refusal.Access AccessRefusal.ResourceUnavailable)
        else
            currentActor principal actor
            |> Result.bind (fun _ ->
                let text =
                    match request.Text with
                    | None -> Ok None
                    | Some raw ->
                        DirectorySearchText.parse raw
                        |> Result.map Some
                        |> Result.mapError (fun violation ->
                            Refusal.invalidInput (InputTarget.DirectorySearch, violation))

                text
                |> Result.bind (fun text ->
                    cursor
                        (Cursors.decodeActors
                            Paging.MaximumActorCursorCharacters
                            installation
                            text
                            request.Role)
                        request.AfterCursor
                    |> Result.map (fun after -> text, after)))

    let private reviewedActors
        (services: ServiceContext)
        (tx: IReadTx)
        (viewer: AuthorityView)
        (actors: Actor list)
        =
        task {
            let ids = actors |> List.map _.ActorId |> List.distinct
            let! summaries = tx.ActorRoleSummaries ids

            let valid =
                (summaries |> Map.keys |> Set.ofSeq) = Set.ofList ids
                && (summaries |> Map.forall (fun _ roles -> ActorRoleSummary.valid roles))

            let items =
                if valid then
                    actors
                    |> List.map (fun actor ->
                        ActorReviewItem.ofActor (Map.find actor.ActorId summaries) actor)
                else
                    []

            match valid, Authority.visibility services.Principal viewer, viewer.Actor with
            | false, _, _ -> return Error CoreFault.StoreIntegrityError
            | true, None, _ -> return Ok items
            | true, Some visibility, Some requester ->
                let ids = actors |> List.map _.ActorId |> List.distinct

                let selection =
                    {
                        CaseSelection.DateBasis = ServiceContext.basis services tx.ObservedAt
                        Visibility = visibility
                        ActorId = requester.ActorId
                        ReadRoles = Authority.readRoles services.Principal
                        ForbiddenRoles = Authority.invalidRoles services.Principal
                        Filter =
                            { CaseListFilter.empty with
                                View = Some CaseView.All
                            }
                    }

                let! counts = tx.ResponsibilityCounts(selection, ids)

                if
                    (counts |> Map.keys |> Set.ofSeq) <> Set.ofList ids
                    || (counts
                        |> Map.exists (fun _ count -> count < 0L || count > SafeInteger.Maximum))
                then
                    return Error CoreFault.StoreIntegrityError
                else
                    return
                        items
                        |> List.map (fun item ->
                            { item with
                                ResponsibleCases =
                                    Some
                                        {
                                            Count = Map.find item.Actor.ActorId counts
                                            ResponsibleActor = item.Actor.ActorId
                                        }
                            })
                        |> Ok
            | true, Some _, None -> return Error CoreFault.StoreIntegrityError
        }

    let actors (services: ServiceContext) (request: ActorListRequest) token =
        task {
            if not (validLimit request.Limit) then
                return! RequestRefusals.invalidLimit services token
            else
                let! result =
                    services.Store.Read
                        {
                            Work =
                                fun tx ->
                                    task {
                                        let! viewer =
                                            tx.Authority
                                                {
                                                    Principal = services.Principal
                                                    CaseId = None
                                                }

                                        match
                                            selectActors
                                                services.InstallationId
                                                services.Principal
                                                request
                                                viewer
                                        with
                                        | Error refusal -> return Outcome.Rejected refusal
                                        | Ok(text, after) ->
                                            let! page =
                                                tx.ActorPage
                                                    {
                                                        Role = request.Role
                                                        Text = text
                                                        AfterActorId = after
                                                        Limit = request.Limit
                                                    }

                                            if page.Items.Length > Paging.MaximumPageSize then
                                                return Outcome.Failed CoreFault.StoreIntegrityError
                                            else
                                                let! items =
                                                    reviewedActors services tx viewer page.Items

                                                return
                                                    items
                                                    |> Result.map (fun items ->
                                                        Outcome.Succeeded
                                                            {
                                                                ActorReviewPage.Basis =
                                                                    ServiceContext.basis
                                                                        services
                                                                        tx.ObservedAt
                                                                Items = items
                                                                NextCursor =
                                                                    page.Next
                                                                    |> Option.map (
                                                                        Cursors.encodeActors
                                                                            services.InstallationId
                                                                            text
                                                                            request.Role
                                                                    )
                                                            })
                                                    |> Result.defaultWith Outcome.Failed
                                    }
                            CancellationToken = token
                        }

                return readResult result
        }

    let selectGrants
        installation
        principal
        (request: GrantsRequest)
        (actor: AuthorityView)
        (target: Actor option)
        =
        if not (Authority.can principal actor Capability.ReadAuthority Scope.Installation) then
            Error(Refusal.Access AccessRefusal.ResourceUnavailable)
        else
            match currentActor principal actor, target with
            | Ok _, Some target when ActorId.value target.ActorId = request.ActorId ->
                cursor (Cursors.decodeGrants installation target.ActorId) request.AfterCursor
            | _ -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)

    let grants (services: ServiceContext) (request: GrantsRequest) token =
        task {
            if not (validLimit request.Limit) then
                return! RequestRefusals.invalidLimit services token
            else
                match ActorId.create request.ActorId with
                | None -> return unavailable
                | Some actorId ->
                    let! result =
                        services.Store.Read
                            {
                                Work =
                                    fun tx ->
                                        task {
                                            let! viewer =
                                                tx.Authority
                                                    {
                                                        Principal = services.Principal
                                                        CaseId = None
                                                    }

                                            let! target = tx.FindActor actorId

                                            match
                                                selectGrants
                                                    services.InstallationId
                                                    services.Principal
                                                    request
                                                    viewer
                                                    target
                                            with
                                            | Error refusal -> return Outcome.Rejected refusal
                                            | Ok after ->
                                                match target with
                                                | None ->
                                                    return
                                                        Outcome.Failed CoreFault.StoreIntegrityError
                                                | Some target ->
                                                    let! page =
                                                        tx.GrantPage
                                                            {
                                                                ActorId = actorId
                                                                AfterGrant = after
                                                                Limit = request.Limit
                                                            }

                                                    if
                                                        page.Items.Length > Paging.MaximumPageSize
                                                    then
                                                        return
                                                            Outcome.Failed
                                                                CoreFault.StoreIntegrityError
                                                    else
                                                        let! targets =
                                                            reviewedActors
                                                                services
                                                                tx
                                                                viewer
                                                                [ target ]

                                                        let! authors =
                                                            tx.ActorRefs(
                                                                page.Items
                                                                |> List.map _.GrantedBy
                                                                |> List.distinct
                                                            )

                                                        let items =
                                                            page.Items
                                                            |> List.map (fun row ->
                                                                Map.tryFind row.GrantedBy authors
                                                                |> Option.map (fun author ->
                                                                    {
                                                                        GrantReadModel.Role =
                                                                            row.Grant.Role
                                                                        Scope = row.Grant.Scope
                                                                        GrantedAt = row.GrantedAt
                                                                        GrantedBy = author
                                                                    })
                                                                |> Option.map Ok
                                                                |> Option.defaultValue (
                                                                    Error
                                                                        CoreFault
                                                                            .StoreIntegrityError
                                                                ))
                                                            |> QueryResults.collect

                                                        return
                                                            targets
                                                            |> Result.bind (fun targets ->
                                                                items
                                                                |> Result.map (fun items ->
                                                                    Outcome.Succeeded
                                                                        {
                                                                            AuthorityGrantPage.Basis =
                                                                                ServiceContext
                                                                                    .basis
                                                                                    services
                                                                                    tx.ObservedAt
                                                                            Actor =
                                                                                List.exactlyOne
                                                                                    targets
                                                                            Items = items
                                                                            NextCursor =
                                                                                page.Next
                                                                                |> Option.map (
                                                                                    Cursors
                                                                                        .encodeGrants
                                                                                        services
                                                                                            .InstallationId
                                                                                        actorId
                                                                                )
                                                                        }))
                                                            |> Result.defaultWith Outcome.Failed
                                        }
                                CancellationToken = token
                            }

                    return readResult result
        }

    let private selectHistory
        installation
        principal
        actor
        (request: AuthorityHistoryRequest)
        (target: ActorId option)
        =
        if
            not (Authority.can principal actor Capability.ReadAuthority Scope.Installation)
            || (request.TargetActorId.IsSome && target.IsNone)
        then
            Error(Refusal.Access AccessRefusal.ResourceUnavailable)
        else
            currentActor principal actor
            |> Result.bind (fun _ ->
                cursor (Cursors.decodeAuthorityHistory installation target) request.AfterCursor)

    let private historyEntries
        installation
        (query: AuthorityHistoryPageQuery)
        actors
        (page: Page<StoredEntry, Sequence>)
        =
        let ordered =
            page.Items
            |> List.pairwise
            |> List.forall (fun (first, second) -> first.Sequence > second.Sequence)

        let continued =
            page.Next
            |> Option.forall (fun next ->
                page.Items |> List.tryLast |> Option.exists (fun entry -> entry.Sequence = next))

        if page.Items.Length > query.Limit || not ordered || not continued then
            Error CoreFault.StoreIntegrityError
        else
            page.Items
            |> List.map (fun entry ->
                LedgerEntries.validate installation entry
                |> Result.bind (LedgerEntries.authorityHistory actors)
                |> Result.bind (fun value ->
                    if
                        (query.TargetActorId |> Option.forall ((=) value.Target.ActorId))
                        && (query.BeforeSequence
                            |> Option.forall (fun before -> value.Sequence < before))
                    then
                        Ok value
                    else
                        Error CoreFault.StoreIntegrityError))
            |> collect

    let history (services: ServiceContext) (request: AuthorityHistoryRequest) token =
        task {
            if not (validLimit request.Limit) then
                return! RequestRefusals.invalidLimit services token
            else
                let target = request.TargetActorId |> Option.bind ActorId.create

                let! result =
                    services.Store.Read(
                        {
                            ReadOperation.Work =
                                fun tx ->
                                    task {
                                        let! actor =
                                            tx.Authority
                                                {
                                                    Principal = services.Principal
                                                    CaseId = None
                                                }

                                        match
                                            selectHistory
                                                services.InstallationId
                                                services.Principal
                                                actor
                                                request
                                                target
                                        with
                                        | Error refusal -> return Outcome.Rejected refusal
                                        | Ok before ->
                                            let query =
                                                {
                                                    AuthorityHistoryPageQuery.TargetActorId = target
                                                    BeforeSequence = before
                                                    Limit = request.Limit
                                                }

                                            let! page = tx.AuthorityHistoryPage query

                                            let ids =
                                                page.Items
                                                |> List.collect (fun entry ->
                                                    entry.ActorId
                                                    :: (match entry.Subject with
                                                        | EntryTarget.Authority(id, _) -> [ id ]
                                                        | _ -> []))
                                                |> List.distinct

                                            let! actors = tx.ActorRefs ids

                                            return
                                                historyEntries
                                                    services.InstallationId
                                                    query
                                                    actors
                                                    page
                                                |> Result.map (fun entries ->
                                                    {
                                                        AuthorityHistoryPage.Basis =
                                                            ServiceContext.basis
                                                                services
                                                                tx.ObservedAt
                                                        Entries = entries
                                                        NextCursor =
                                                            page.Next
                                                            |> Option.map (
                                                                Cursors.encodeAuthorityHistory
                                                                    services.InstallationId
                                                                    target
                                                            )
                                                    })
                                                |> succeeded
                                    }
                            CancellationToken = token
                        }
                    )

                return readResult result
        }
