namespace ClaimCore.Application.Reads

open ClaimCore.Application.Storage

open ClaimCore.Application.Execution

open ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Every read authorizes and decodes its continuation inside one store snapshot.
module internal PeopleQueries =
    open QueryResults

    let selectActors installation principal (request: ActorListRequest) actor =
        if not (Access.can principal actor Capability.ReadAccess Scope.Installation) then
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
        (tx: ReadTx)
        (viewer: AccessView)
        (actors: Actor list)
        =
        task {
            let ids = actors |> List.map _.ActorId |> List.distinct
            let! summaries = PeopleReads.actorRoleSummaries tx ids

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

            match valid, Access.visibility services.Principal viewer, viewer.Actor with
            | false, _, _ -> return Error CoreFault.StoreIntegrityError
            | true, None, _ -> return Ok items
            | true, Some visibility, Some requester ->
                let ids = actors |> List.map _.ActorId |> List.distinct

                let selection =
                    {
                        CaseSelection.DateBasis = ServiceContext.basis services tx.ObservedAt
                        Visibility = visibility
                        ActorId = requester.ActorId
                        ReadRoles = Access.readRoles services.Principal
                        ForbiddenRoles = Access.invalidRoles services.Principal
                        Filter =
                            { CaseListFilter.activeOpen with
                                View = Some CaseView.All
                            }
                    }

                let! counts = WorkReads.responsibilityCounts tx (selection, ids)

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
                                            AccessReads.access tx (services.Principal) (None)

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
                                                PeopleReads.actorPage
                                                    tx
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
        (actor: AccessView)
        (target: Actor option)
        =
        if not (Access.can principal actor Capability.ReadAccess Scope.Installation) then
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
                                                AccessReads.access tx (services.Principal) (None)

                                            let! target = PeopleReads.findActor tx actorId

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
                                                    let! owned =
                                                        PeopleReads.peopleRevision
                                                            tx
                                                            target.Principal

                                                    match owned with
                                                    | None ->
                                                        return
                                                            Outcome.Failed
                                                                CoreFault.StoreIntegrityError
                                                    | Some revision ->
                                                        let! maintenance =
                                                            RegisterReads.maintenance tx ()

                                                        let offers =
                                                            Advice.actorOffers
                                                                services.Principal
                                                                viewer
                                                                maintenance

                                                        let! requester =
                                                            ExecutionEnvironment.requester
                                                                tx
                                                                services.Principal

                                                        let mutable controls = []

                                                        for id in offers do
                                                            let selected =
                                                                ClaimCore.Domain.Tasks.Registry.find
                                                                    id
                                                                |> Option.get

                                                            let binding =
                                                                Binding.People(actorId, revision)

                                                            let! snapshot =
                                                                ExecutionEnvironment.fromStorage
                                                                    tx
                                                                    services.Principal
                                                                    requester
                                                                    maintenance.Active
                                                                    selected
                                                                    binding
                                                                    (Some InputMap.empty)
                                                                    None

                                                            let available =
                                                                snapshot.Availability binding

                                                            if available.Blocked.IsNone then
                                                                controls <-
                                                                    TaskOffer.Available(
                                                                        id,
                                                                        binding,
                                                                        available
                                                                    )
                                                                    :: controls

                                                        let controls = List.rev controls

                                                        let profileSeed =
                                                            if
                                                                offers
                                                                |> List.exists (fun id ->
                                                                    ClaimCore.Domain.Tasks
                                                                        .Registry.findDeclaration
                                                                        id
                                                                    |> Option.exists
                                                                        (fun declaration ->
                                                                            declaration.Inputs
                                                                            |> List.exists
                                                                                (fun input ->
                                                                                    input.Input =
                                                                                        TaskInputId
                                                                                            .Target
                                                                                            InputTarget
                                                                                                .ProfileDisplayName
                                                                                    && input
                                                                                        .Source
                                                                                        =
                                                                                        InputSource
                                                                                            .Authored)))
                                                            then
                                                                target.Profile
                                                                |> Option.map ActorProfile.authored
                                                            else
                                                                None

                                                        let! page =
                                                            PeopleReads.grantPage
                                                                tx
                                                                {
                                                                    ActorId = actorId
                                                                    AfterGrant = after
                                                                    Limit = request.Limit
                                                                }

                                                        if
                                                            page.Items.Length >
                                                                Paging.MaximumPageSize
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
                                                                PeopleReads.actorRefs
                                                                    tx
                                                                    (page.Items
                                                                     |> List.map _.GrantedBy
                                                                     |> List.distinct)

                                                            let rowControls (grant: ScopedGrant) =
                                                                let selection =
                                                                    [
                                                                        TaskInputId.Target
                                                                            InputTarget.Role,
                                                                        InputValue.Text(
                                                                            Role.token grant.Role
                                                                        )
                                                                        TaskInputId.Scope,
                                                                        InputValue.Text(
                                                                            ScopeKind.token (
                                                                                match
                                                                                    grant.Scope
                                                                                with
                                                                                | GrantScope.Installation ->
                                                                                    ScopeKind
                                                                                        .Installation
                                                                                | GrantScope.Case _ ->
                                                                                    ScopeKind.Case
                                                                            )
                                                                        )
                                                                        match grant.Scope with
                                                                        | GrantScope.Case(id, _) ->
                                                                            TaskInputId.CaseId,
                                                                            InputValue.Text(
                                                                                CaseId.format id
                                                                            )
                                                                        | GrantScope.Installation ->
                                                                            ()
                                                                    ]
                                                                    |> Map.ofList

                                                                controls
                                                                |> List.choose (function
                                                                    | TaskOffer.Available(task,
                                                                                          binding,
                                                                                          available) when
                                                                        Set.contains
                                                                            selection
                                                                            available.Selections
                                                                        ->
                                                                        Some(
                                                                            TaskOffer.Available(
                                                                                task,
                                                                                binding,
                                                                                { available with
                                                                                    Seeds =
                                                                                        selection
                                                                                    Editable = []
                                                                                    Selections =
                                                                                        Set.empty
                                                                                }
                                                                            )
                                                                        )
                                                                    | _ -> None)

                                                            let items =
                                                                page.Items
                                                                |> List.map (fun row ->
                                                                    Map.tryFind
                                                                        row.GrantedBy
                                                                        authors
                                                                    |> Option.map (fun author ->
                                                                        {
                                                                            GrantReadModel.Controls =
                                                                                rowControls
                                                                                    row.Grant
                                                                            Role = row.Grant.Role
                                                                            Scope =
                                                                                row.Grant.Scope
                                                                            GrantedAt =
                                                                                row.GrantedAt
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
                                                                                PeopleGrantPage.Basis =
                                                                                    ServiceContext
                                                                                        .basis
                                                                                        services
                                                                                        tx
                                                                                            .ObservedAt
                                                                                Actor =
                                                                                    List
                                                                                        .exactlyOne
                                                                                        targets
                                                                                Revision =
                                                                                    revision
                                                                                Offers = offers
                                                                                Controls =
                                                                                    controls
                                                                                ProfileSeed =
                                                                                    profileSeed
                                                                                CanTerminateSessions =
                                                                                    Access.can
                                                                                        services
                                                                                            .Principal
                                                                                        viewer
                                                                                        Capability
                                                                                            .ManageAccess
                                                                                        Scope
                                                                                            .Installation
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
        (request: PeopleHistoryRequest)
        (target: ActorId option)
        =
        if
            not (Access.can principal actor Capability.ReadAccess Scope.Installation)
            || (request.TargetActorId.IsSome && target.IsNone)
        then
            Error(Refusal.Access AccessRefusal.ResourceUnavailable)
        else
            currentActor principal actor
            |> Result.bind (fun _ ->
                cursor (Cursors.decodePeopleHistory installation target) request.AfterCursor)

    let history (services: ServiceContext) (request: PeopleHistoryRequest) token =
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
                                            AccessReads.access tx (services.Principal) (None)

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
                                            let query: TransactionPages.PeopleHistoryQuery =
                                                {
                                                    TransactionPages.PeopleHistoryQuery.TargetActorId =
                                                        target
                                                    BeforePosition = before
                                                    Limit = request.Limit
                                                }

                                            let! page = PeopleReads.peopleHistoryPage tx query

                                            let ordered =
                                                page.Items
                                                |> List.pairwise
                                                |> List.forall (fun (first, second) ->
                                                    first.Position > second.Position)

                                            let continued =
                                                page.Next
                                                |> Option.forall (fun next ->
                                                    page.Items
                                                    |> List.tryLast
                                                    |> Option.exists (fun entry ->
                                                        entry.Position = next))

                                            let inScope =
                                                page.Items
                                                |> List.forall (fun entry ->
                                                    (query.BeforePosition
                                                     |> Option.forall (fun before ->
                                                         entry.Position < before))
                                                    && (match entry.Change.Aggregate with
                                                        | AggregateReference.Person id ->
                                                            query.TargetActorId
                                                            |> Option.forall ((=) id)
                                                        | _ -> false))

                                            if
                                                page.Items.Length > query.Limit
                                                || not ordered
                                                || not continued
                                                || not inScope
                                            then
                                                return Outcome.Failed CoreFault.StoreIntegrityError
                                            else
                                                let! described =
                                                    PeopleHistoryReads.read tx page.Items

                                                return
                                                    described
                                                    |> Result.map (fun entries ->
                                                        {
                                                            PeopleHistoryPage.Basis =
                                                                ServiceContext.basis
                                                                    services
                                                                    tx.ObservedAt
                                                            Entries = entries
                                                            NextCursor =
                                                                page.Next
                                                                |> Option.map (
                                                                    Cursors.encodePeopleHistory
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
