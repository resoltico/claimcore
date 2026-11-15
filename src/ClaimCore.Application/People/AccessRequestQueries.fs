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
                                        match
                                            cursor
                                                (Cursors.decodeAccessRequests
                                                    Paging.MaximumAccessRequestCursorCharacters
                                                    services.InstallationId)
                                                request.AfterCursor
                                        with
                                        | Error refusal -> return Outcome.Rejected refusal
                                        | Ok after ->
                                            let! result =
                                                PeopleReads.accessRequestPage
                                                    tx
                                                    {
                                                        After = after
                                                        ObservedAt = tx.ObservedAt
                                                        Limit = request.Limit
                                                    }

                                            if result.Items.Length > request.Limit then
                                                return Outcome.Failed CoreFault.StoreIntegrityError
                                            else
                                                return
                                                    Outcome.Succeeded
                                                        {
                                                            AccessRequestPage.Basis =
                                                                ServiceContext.basis
                                                                    services
                                                                    tx.ObservedAt
                                                            Items = result.Items
                                                            NextCursor =
                                                                result.Next
                                                                |> Option.map (
                                                                    Cursors.encodeAccessRequests
                                                                        services.InstallationId
                                                                )
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
                                            do! tx.DismissAccessRequest principal
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
