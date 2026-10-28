namespace ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.RecordFormat

/// Every read authorizes and decodes its continuation inside one store snapshot.
module internal AuthorityQueries =
    open QueryResults

    let selectActors principal (request: PageRequest) actor =
        if not (Authority.can principal actor Capability.ReadAuthority Scope.Installation) then
            Error Refusal.ResourceUnavailable
        else
            currentActor principal actor
            |> Result.bind (fun _ -> cursor Cursors.decodeActors request.AfterCursor)

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

                                        match selectActors principal request actor with
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
                                                                |> Option.map Cursors.encodeActors
                                                        }
                                    })
                            CancellationToken = token
                        }
                    )

                return readResult result
        }

    let selectGrants
        principal
        (request: GrantsRequest)
        (actor: AuthorityView)
        (target: Actor option)
        =
        if not (Authority.can principal actor Capability.ReadAuthority Scope.Installation) then
            Error Refusal.ResourceUnavailable
        else
            match currentActor principal actor, target with
            | Ok _, Some target when target.ActorId = request.ActorId ->
                cursor (Cursors.decodeGrants request.ActorId) request.AfterCursor
            | _ -> Error Refusal.ResourceUnavailable

    let grants (services: ServiceContext) (request: GrantsRequest) token =
        let store = services.Store
        let principal = services.Principal

        task {
            if not (validLimit request.Limit) then
                return invalidLimit ()
            elif request.ActorId = Guid.Empty then
                return unavailable
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

                                        let! target = tx.FindActor request.ActorId

                                        match selectGrants principal request actor target with
                                        | Error refusal -> return Outcome.Rejected refusal
                                        | Ok after ->
                                            match target with
                                            | None ->
                                                return Outcome.Failed CoreFault.StoreIntegrityError
                                            | Some target ->
                                                let! page =
                                                    tx.GrantPage(
                                                        {
                                                            GrantPageQuery.ActorId =
                                                                request.ActorId
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
                                                                            request.ActorId
                                                                    )
                                                            }
                                    })
                            CancellationToken = token
                        }
                    )

                return readResult result
        }
