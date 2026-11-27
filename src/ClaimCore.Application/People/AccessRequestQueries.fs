namespace ClaimCore.Application.Reads

open ClaimCore.Application.Storage

open ClaimCore.Application.Execution

open ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Access-request hints confer no access; only access managers can inspect or dismiss them.
module internal AccessRequestQueries =
    open QueryResults

    let page (services: ServiceContext) (request: PageRequest) token =
        task {
            let! result =
                DisclosureReads.pageRead
                    services
                    DisclosureKind.AccessRequests
                    (fun _ -> None)
                    (fun (page: AccessRequestPage) -> DisclosureReads.rows page.Items)
                    {
                        CancellationToken = token
                        Work =
                            fun tx ->
                                task {
                                    let! actor = AccessReads.access tx (services.Principal) (None)

                                    if
                                        not (
                                            Access.can
                                                services.Principal
                                                actor
                                                Capability.ManageAccess
                                                Scope.Installation
                                        )
                                    then
                                        return unavailable
                                    elif not (validLimit request.Limit) then
                                        return invalidLimit ()
                                    else
                                        let requester = actor.Actor |> Option.get |> _.ActorId

                                        let traversal =
                                            ScopedTraversal.pageWithBasis
                                                tx
                                                "access-request-traversal"
                                                Paging.MaximumAccessRequestCursorCharacters
                                                requester
                                                None
                                                (Cursors.decodeAccessRequests
                                                    Paging.MaximumAccessRequestCursorCharacters
                                                    services.InstallationId)
                                                (Cursors.encodeAccessRequests
                                                    services.InstallationId)
                                                (fun () ->
                                                    task {
                                                        let! generation, expires =
                                                            AccessRequests.traversalBasis
                                                                tx.Connection
                                                                tx.ObservedAt
                                                                token

                                                        return
                                                            {
                                                                Generation =
                                                                    generation.ToString(
                                                                        System.Globalization
                                                                            .CultureInfo
                                                                            .InvariantCulture
                                                                    )
                                                                ValidUntil =
                                                                    defaultArg
                                                                        expires
                                                                        System.DateTimeOffset
                                                                            .MaxValue
                                                            }
                                                            : ScopedTraversal.Basis
                                                    })
                                                (fun () ->
                                                    AccessRequests.witness
                                                        tx.Connection
                                                        tx.ObservedAt
                                                        token)
                                                (fun _ after ->
                                                    PeopleReads.accessRequestPage
                                                        tx
                                                        {
                                                            After = after
                                                            ObservedAt = tx.ObservedAt
                                                            Limit = request.Limit
                                                        })
                                                false
                                                _.Next
                                                request.AfterCursor

                                        match! traversal with
                                        | Error refusal -> return Outcome.Rejected refusal
                                        | Ok(page, _) when page.Items.Length > request.Limit ->
                                            return Outcome.Failed CoreFault.StoreIntegrityError
                                        | Ok(page, next) ->
                                            return
                                                Outcome.Succeeded
                                                    {
                                                        AccessRequestPage.Basis =
                                                            ServiceContext.basis
                                                                services
                                                                tx.ObservedAt
                                                        Items = page.Items
                                                        NextCursor = next
                                                    }

                                }
                    }

            return result
        }

    let submit (services: ServiceContext) () token =
        task {
            let! result =
                services.Store.Write
                    {
                        WriteOperation.RequiresEncryption = false
                        CancellationToken = token
                        Work =
                            fun tx ->
                                task {
                                    let principal = services.Principal

                                    let! _ =
                                        PrincipalLock.acquire
                                            tx.Connection
                                            principal
                                            tx.CancellationToken

                                    let! owner = PersonAcquisition.principalOwner tx principal

                                    match owner with
                                    | Some _ ->
                                        return
                                            Verdict.Refuse(
                                                Refusal.People PeopleRefusal.ActorAlreadyRegistered
                                            )
                                    | None ->
                                        do!
                                            AccessRequests.refresh
                                                tx.Connection
                                                principal
                                                services.Claims
                                                tx.ObservedAt
                                                tx.CancellationToken

                                        return Verdict.Proceed()
                                }
                    }

            return
                result
                |> Result.map (function
                    | Verdict.Proceed() -> Outcome.Succeeded()
                    | Verdict.Replay _ -> Outcome.Failed CoreFault.StoreIntegrityError
                    | Verdict.Refuse refusal -> Outcome.Rejected refusal
                    | Verdict.Fail fault -> Outcome.Failed fault)
                |> readResult
        }

    let dismiss (services: ServiceContext) (input: PrincipalInput) token =
        task {
            let! result =
                services.Store.Write
                    {
                        WriteOperation.RequiresEncryption = false
                        CancellationToken = token
                        Work =
                            fun tx ->
                                task {
                                    let! actor = AccessReads.access tx (services.Principal) (None)

                                    if
                                        not (
                                            Access.can
                                                services.Principal
                                                actor
                                                Capability.ManageAccess
                                                Scope.Installation
                                        )
                                    then
                                        return
                                            Verdict.Refuse(
                                                Refusal.Access AccessRefusal.ResourceUnavailable
                                            )
                                    else
                                        match
                                            PrincipalKey.ofKind
                                                input.Kind
                                                input.Issuer
                                                input.Subject
                                        with
                                        | Error issues -> return Verdict.Refuse issues
                                        | Ok principal ->
                                            let! _ =
                                                PrincipalLock.acquire
                                                    tx.Connection
                                                    principal
                                                    tx.CancellationToken

                                            let! _ =
                                                AccessRequests.delete
                                                    tx.Connection
                                                    principal
                                                    tx.CancellationToken

                                            return Verdict.Proceed()
                                }
                    }

            return
                result
                |> Result.map (function
                    | Verdict.Proceed() -> Outcome.Succeeded()
                    | Verdict.Replay _ -> Outcome.Failed CoreFault.StoreIntegrityError
                    | Verdict.Refuse refusal -> Outcome.Rejected refusal
                    | Verdict.Fail fault -> Outcome.Failed fault)
                |> readResult
        }
