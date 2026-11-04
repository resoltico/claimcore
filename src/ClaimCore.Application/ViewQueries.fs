namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// View counts acquire current authority and the date basis inside the same read snapshot.
module internal ViewQueries =
    let private selection (services: ServiceContext) (tx: IReadTx) actor =
        QueryResults.currentActor services.Principal actor
        |> Result.bind (fun _ ->
            match Authority.visibility services.Principal actor, actor.Actor with
            | Some visibility, Some requester ->
                Ok
                    {
                        CaseSelection.DateBasis = ServiceContext.basis services tx.ObservedAt
                        Visibility = visibility
                        ActorId = requester.ActorId
                        ReadRoles = Authority.readRoles services.Principal
                        ForbiddenRoles = Authority.invalidRoles services.Principal
                        Filter = CaseListFilter.empty
                    }
            | _ -> Error(Refusal.Access AccessRefusal.ResourceUnavailable))

    let private counts (services: ServiceContext) query token =
        task {
            let! result =
                services.Store.Read
                    {
                        Work =
                            fun tx ->
                                task {
                                    let! actor =
                                        tx.Authority
                                            {
                                                Principal = services.Principal
                                                CaseId = None
                                            }

                                    match selection services tx actor with
                                    | Error refusal -> return Outcome.Rejected refusal
                                    | Ok selection ->
                                        let! counts = query tx selection
                                        return Outcome.Succeeded counts
                                }
                        CancellationToken = token
                    }

            return QueryResults.readResult result
        }

    let cases services () token =
        counts
            services
            (fun tx selection ->
                task {
                    let! items = tx.CaseViewCounts selection

                    return
                        {
                            CaseViews.Basis = selection.DateBasis
                            Items = items
                        }
                })
            token

    let reminders services () token =
        counts
            services
            (fun tx selection ->
                task {
                    let! items = tx.ReminderViewCounts selection

                    return
                        {
                            ReminderViews.Basis = selection.DateBasis
                            Items = items
                        }
                })
            token
