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
                services.Store.Read
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
                                                requester
                                                None
                                                (Cursors.decodeAccessRequests
                                                    Paging.MaximumAccessRequestCursorCharacters
                                                    services.InstallationId)
                                                (Cursors.encodeAccessRequests
                                                    services.InstallationId)
                                                (fun () ->
                                                    AccessRequests.witness
                                                        tx.Connection
                                                        tx.ObservedAt
                                                        token)
                                                (fun after ->
                                                    PeopleReads.accessRequestPage
                                                        tx
                                                        {
                                                            After = after
                                                            ObservedAt = tx.ObservedAt
                                                            Limit = request.Limit
                                                        })
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

            return readResult result
        }

    let dismiss (services: ServiceContext) (input: PrincipalInput) token =
        task {
            let! result =
                services.Store.Write
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
