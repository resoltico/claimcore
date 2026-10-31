namespace ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Every read authorizes and decodes its continuation inside one store snapshot.
module internal AuthorityQueries =
    open QueryResults

    let selectActors installation principal (request: PageRequest) actor =
        if not (Authority.can principal actor Capability.ReadAuthority Scope.Installation) then
            Error(Refusal.Access AccessRefusal.ResourceUnavailable)
        else
            currentActor principal actor
            |> Result.bind (fun _ -> cursor (Cursors.decodeActors installation) request.AfterCursor)

    let actors (services: ServiceContext) (request: PageRequest) token =
        let store = services.Store
        let principal = services.Principal

        task {
            if not (validLimit request.Limit) then
                return invalidLimit ()
            else
                let! result =
                    store.Read(
                        {
                            ReadOperation.Work =
                                (fun tx ->
                                    task {
                                        let! actor =
                                            tx.Authority(
                                                {
                                                    AuthorityQuery.Principal = principal
                                                    CaseId = None
                                                }
                                            )

                                        match
                                            selectActors
                                                services.InstallationId
                                                principal
                                                request
                                                actor
                                        with
                                        | Error refusal -> return Outcome.Rejected refusal
                                        | Ok after ->
                                            let! page =
                                                tx.ActorPage(
                                                    {
                                                        ActorPageQuery.AfterActorId = after
                                                        Limit = request.Limit
                                                    }
                                                )

                                            if page.Items.Length > Paging.MaximumPageSize then
                                                return Outcome.Failed CoreFault.StoreIntegrityError
                                            else
                                                return
                                                    Outcome.Succeeded
                                                        {
                                                            ActorReviewPage.Items = page.Items

                                                            NextCursor =
                                                                page.Next
                                                                |> Option.map (
                                                                    Cursors.encodeActors
                                                                        services.InstallationId
                                                                )
                                                        }
                                    })
                            CancellationToken = token
                        }
                    )

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
        let store = services.Store
        let principal = services.Principal

        let actorId = ActorId.create request.ActorId

        task {
            if not (validLimit request.Limit) then
                return invalidLimit ()
            elif actorId.IsNone then
                return unavailable
            else
                let actorId = actorId.Value

                let! result =
                    store.Read(
                        {
                            ReadOperation.Work =
                                (fun tx ->
                                    task {
                                        let! actor =
                                            tx.Authority(
                                                {
                                                    AuthorityQuery.Principal = principal
                                                    CaseId = None
                                                }
                                            )

                                        let! target = tx.FindActor actorId

                                        match
                                            selectGrants
                                                services.InstallationId
                                                principal
                                                request
                                                actor
                                                target
                                        with
                                        | Error refusal -> return Outcome.Rejected refusal
                                        | Ok after ->
                                            match target with
                                            | None ->
                                                return Outcome.Failed CoreFault.StoreIntegrityError
                                            | Some target ->
                                                let! page =
                                                    tx.GrantPage(
                                                        {
                                                            GrantPageQuery.ActorId = actorId
                                                            AfterGrant = after
                                                            Limit = request.Limit
                                                        }
                                                    )

                                                if page.Items.Length > Paging.MaximumPageSize then
                                                    return
                                                        Outcome.Failed
                                                            CoreFault.StoreIntegrityError
                                                else
                                                    return
                                                        Outcome.Succeeded
                                                            {
                                                                AuthorityGrantPage.Actor = target
                                                                Items = page.Items
                                                                NextCursor =
                                                                    page.Next
                                                                    |> Option.map (
                                                                        Cursors.encodeGrants
                                                                            services.InstallationId
                                                                            actorId
                                                                    )
                                                            }
                                    })
                            CancellationToken = token
                        }
                    )

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
                |> Result.bind LedgerEntries.authorityHistory
                |> Result.bind (fun value ->
                    if
                        (query.TargetActorId |> Option.forall ((=) value.TargetActorId))
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
                return invalidLimit ()
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

                                            return
                                                historyEntries services.InstallationId query page
                                                |> Result.map (fun entries ->
                                                    {
                                                        AuthorityHistoryPage.Entries = entries
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
