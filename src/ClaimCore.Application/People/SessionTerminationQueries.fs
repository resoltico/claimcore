namespace ClaimCore.Application.Reads

open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.Domain

/// The head lock supplies current authorization; principal locking serializes creation and deletion.
module internal SessionTerminationQueries =
    let private terminate (services: ServiceContext) target token =
        task {
            let! result =
                services.Store.Write
                    {
                        CancellationToken = token
                        Work =
                            fun tx ->
                                task {
                                    let! selected =
                                        task {
                                            match target with
                                            | None -> return Ok services.Principal
                                            | Some raw ->
                                                let! access =
                                                    AccessReads.access tx services.Principal None

                                                if
                                                    not (
                                                        Access.can
                                                            services.Principal
                                                            access
                                                            Capability.ManageAccess
                                                            Scope.Installation
                                                    )
                                                then
                                                    return
                                                        Error(
                                                            Refusal.Access
                                                                AccessRefusal.ResourceUnavailable
                                                        )
                                                else
                                                    match ActorId.create raw with
                                                    | None ->
                                                        return
                                                            Error(
                                                                Refusal.Access
                                                                    AccessRefusal
                                                                        .ResourceUnavailable
                                                            )
                                                    | Some id ->
                                                        let! person = AggregateStorage.person tx id

                                                        return
                                                            person
                                                            |> Option.map (fun person ->
                                                                Ok person.Actor.Principal)
                                                            |> Option.defaultValue (
                                                                Error(
                                                                    Refusal.Access
                                                                        AccessRefusal
                                                                            .ResourceUnavailable
                                                                )
                                                            )
                                        }

                                    match selected with
                                    | Error refusal -> return Verdict.Refuse refusal
                                    | Ok principal ->
                                        let! _ =
                                            PrincipalLock.acquire
                                                tx.Connection
                                                principal
                                                tx.CancellationToken

                                        let! count =
                                            SessionRows.terminate
                                                tx.Connection
                                                principal
                                                tx.CancellationToken

                                        return
                                            Verdict.Proceed
                                                {
                                                    Count = int64 count
                                                    Self = principal = services.Principal
                                                }
                                }
                    }

            return
                match result with
                | Error StoreFailure.CommitOutcomeUnknown -> SessionTerminationResult.OutcomeUnknown
                | Error StoreFailure.Cancelled ->
                    SessionTerminationResult.Failed CoreFault.StoreUnavailable
                | Error failure -> SessionTerminationResult.Failed(QueryResults.fault failure)
                | Ok(Verdict.Proceed value) -> SessionTerminationResult.Succeeded value
                | Ok(Verdict.Refuse refusal) -> SessionTerminationResult.Rejected refusal
                | Ok(Verdict.Fail fault) -> SessionTerminationResult.Failed fault
                | Ok(Verdict.Replay _) ->
                    SessionTerminationResult.Failed CoreFault.StoreIntegrityError
        }

    let self services () token = terminate services None token

    let person services (request: PersonSessionTerminationRequest) token =
        terminate services (Some request.ActorId) token
