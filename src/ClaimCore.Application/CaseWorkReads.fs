namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Labels and eligibility are current relation-scoped disclosure, never retained case facts.
module internal CaseWorkReads =
    let basis (services: ServiceContext) observed =
        {
            ObservedAt = observed
            BusinessDate = services.Calendar.DateAt observed
            TimeZoneId = services.Calendar.TimeZoneId
        }

    let view (tx: IReadTx) basis (record: StoredCase) =
        task {
            let fields = CaseState.fields record.Snapshot.State
            let work = fields |> Option.map _.Work |> Option.defaultValue CaseWork.empty

            let! account =
                task {
                    match work.Responsible with
                    | None -> return Ok None
                    | Some id ->
                        let! actor = tx.FindActor id

                        match actor with
                        | Some actor when actor.ActorId = id && PrincipalKey.isHuman actor.Principal ->
                            let! authority =
                                tx.Authority
                                    {
                                        Principal = actor.Principal
                                        CaseId = Some record.CaseId
                                    }

                            let _, _, subject = PrincipalKey.parts actor.Principal

                            return
                                Ok(
                                    Some
                                        {
                                            Account = { ActorId = id; Subject = subject }
                                            Available =
                                                Authority.can
                                                    actor.Principal
                                                    authority
                                                    Capability.ReadCase
                                                    (Scope.Case record.CaseId)
                                                && Authority.can
                                                    actor.Principal
                                                    authority
                                                    Capability.ManageCaseWork
                                                    (Scope.Case record.CaseId)
                                        }
                                )
                        | _ -> return Error CoreFault.StoreIntegrityError
                }

            return
                account
                |> Result.map (fun account ->
                    {
                        Responsible = account
                        FollowUp = work.FollowUp
                        Basis = basis
                        Due =
                            CaseDue.at
                                basis.BusinessDate
                                (CaseState.lifecycle record.Snapshot.State)
                                (fields |> Option.map _.Status)
                                work
                    })
        }
