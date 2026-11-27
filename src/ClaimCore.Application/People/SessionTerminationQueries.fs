namespace ClaimCore.Application.Reads

open System
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.Domain

/// Protected owner authority and identity ownership are resolved under the write head.
module internal SessionTerminationQueries =
    type private Subject =
        | Account of ActorId
        | Principal of PrincipalKey

    let private subjectKey =
        function
        | Account actor -> ActorId.format actor
        | Principal principal -> PrincipalKey.naturalKey principal

    let private unavailable = Refusal.Access AccessRefusal.ResourceUnavailable

    let private select (services: ServiceContext) (tx: WriteTx) target =
        task {
            match target with
            | None ->
                let! state =
                    Credentials.current tx.Connection services.Principal tx.CancellationToken

                let actor = state.Binding |> Option.map (fun (actor, _, _, _) -> actor)

                let usable =
                    state.Binding |> Option.exists (fun (_, enabled, _, _) -> enabled)
                    && Credentials.admitsCurrent tx services.Principal state

                return
                    match OwnSessionScope.select actor usable with
                    | Some OwnSessionScope.Principal ->
                        let caller = Principal services.Principal
                        Ok(caller, caller)
                    | Some(OwnSessionScope.Account actor) -> Ok(Account actor, Account actor)
                    | None -> Error unavailable
            | Some raw ->
                let! access = AccessReads.access tx services.Principal None

                match access.Actor, ActorId.create raw with
                | Some caller, Some id when
                    Access.can services.Principal access Capability.ManageAccess Scope.Installation
                    ->
                    match! PersonAcquisition.person tx id with
                    | Some _ -> return Ok(Account caller.ActorId, Account id)
                    | None -> return Error unavailable
                | _ -> return Error unavailable
        }

    type private Mode =
        | Cut
        | Settle
        | ConfirmRetirement

    type private Completion =
        | Terminated of SessionTermination
        | RetirementCommitted
        | Missing

    let private mode settleOnly confirmRetirement =
        if confirmRetirement = Some true then ConfirmRetirement
        elif settleOnly = Some true then Settle
        else Cut

    let private commit (tx: WriteTx) operation intent caller selected mode =
        task {
            let connection = tx.Connection
            let token = tx.CancellationToken
            let callerKey, selectedKey = subjectKey caller, subjectKey selected
            let! _ = SessionRows.lockOrder connection token
            let! observed = SessionRows.now connection token

            let! frontier = SessionRows.terminationCutoff connection token

            match OperationLifetime.refusal frontier observed operation with
            | Some refusal -> return Verdict.Refuse refusal
            | None when mode = ConfirmRetirement ->
                // Advance retirement without recording a result, ordering a cut or touching tickets.
                do! SessionRows.pruneResults connection observed token
                return Verdict.Proceed RetirementCommitted
            | None ->
                let id = OperationId.value operation

                let! saved =
                    DatabaseSession.rows
                        connection
                        "SELECT requester,intent,selected,removed,self FROM claimcore.session_termination_results WHERE request_id=$1"
                        [| box id |]
                        token

                match saved with
                | [ row ] when
                    DatabaseSession.value<string> 0 row = callerKey
                    && DatabaseSession.value<string> 1 row = intent
                    && DatabaseSession.value<string> 2 row = selectedKey
                    ->
                    return
                        Verdict.Proceed(
                            Terminated
                                {
                                    Count = DatabaseSession.value<int64> 3 row
                                    Self = DatabaseSession.value<bool> 4 row
                                }
                        )
                | [ _ ] -> return Verdict.Refuse Refusal.IdempotencyConflict
                | [] when mode = Settle -> return Verdict.Proceed Missing
                | [] ->
                    let! keys =
                        match selected with
                        | Account actor -> PersonAcquisition.reservedKeys tx actor
                        | Principal principal ->
                            System.Threading.Tasks.Task.FromResult
                                [| PrincipalKey.naturalKey principal |]

                    let! position = SessionRows.advanceOrder connection token
                    let! cut = SessionRows.now connection token
                    let! count = SessionRows.terminate connection position cut keys token
                    let self = caller = selected
                    let issued = OperationId.issuedAt operation
                    do! SessionRows.pruneResults connection observed token

                    let! _ =
                        DatabaseSession.execute
                            connection
                            """INSERT INTO claimcore.session_termination_results(request_id,requested_at,requester,intent,selected,removed,self)
                               VALUES ($1,$2,$3,$4,$5,$6,$7)"""
                            [|
                                box id
                                box issued
                                box callerKey
                                box intent
                                box selectedKey
                                box count
                                box self
                            |]
                            token

                    return Verdict.Proceed(Terminated { Count = int64 count; Self = self })
                | _ -> return Verdict.Fail CoreFault.StoreIntegrityError
        }

    let private terminate (services: ServiceContext) operation target mode token =
        task {
            let! result =
                services.Store.Write
                    {
                        WriteOperation.RequiresEncryption = false
                        CancellationToken = token
                        Work =
                            fun tx ->
                                task {
                                    match! select services tx target with
                                    | Error refusal -> return Verdict.Refuse refusal
                                    | Ok(caller, selected) ->
                                        let intent =
                                            (if target.IsSome then
                                                 CommandTaskId.TerminatePersonSessions
                                             else
                                                 CommandTaskId.TerminateOwnSessions)
                                            |> CommandTaskId.key

                                        match! commit tx operation intent caller selected mode with
                                        | Verdict.Proceed completion ->
                                            return Verdict.Proceed(caller, completion)
                                        | Verdict.Refuse refusal -> return Verdict.Refuse refusal
                                        | Verdict.Fail fault -> return Verdict.Fail fault
                                        | Verdict.Replay _ ->
                                            return Verdict.Fail CoreFault.StoreIntegrityError
                                }
                    }

            match result with
            | Error StoreFailure.CommitOutcomeUnknown ->
                return DisclosureReads.safeTermination (SessionTerminationResult.OutcomeUnknown)
            | Error StoreFailure.Cancelled ->
                return
                    DisclosureReads.safeTermination (
                        SessionTerminationResult.Failed CoreFault.StoreUnavailable
                    )
            | Error failure ->
                return
                    DisclosureReads.safeTermination (
                        SessionTerminationResult.Failed(QueryResults.fault failure)
                    )
            | Ok(Verdict.Proceed(Account actor, Terminated value)) ->
                let event =
                    DisclosureReads.success actor DisclosureKind.SessionTermination None None

                let! released =
                    DisclosureReads.release services token (Outcome.Succeeded value, Some event)

                return DisclosureReads.termination released
            | Ok(Verdict.Proceed(Principal _, Terminated value)) ->
                // Bootstrap sign-outs affect only the principal that authenticated this session.
                return DisclosureReads.bootstrapTermination value
            | Ok(Verdict.Proceed(_, RetirementCommitted)) ->
                return
                    DisclosureReads.safeTermination (SessionTerminationResult.RetirementConfirmed)
            | Ok(Verdict.Proceed(_, Missing)) ->
                return DisclosureReads.safeTermination (SessionTerminationResult.OutcomeUnknown)
            | Ok(Verdict.Refuse refusal) ->
                return DisclosureReads.safeTermination (SessionTerminationResult.Rejected refusal)
            | Ok(Verdict.Fail fault) ->
                return DisclosureReads.safeTermination (SessionTerminationResult.Failed fault)
            | Ok(Verdict.Replay _) ->
                return
                    DisclosureReads.safeTermination (
                        SessionTerminationResult.Failed CoreFault.StoreIntegrityError
                    )

        }

    let self services (request: OwnSessionTerminationRequest) token =
        terminate
            services
            request.OperationId
            None
            (mode request.SettleOnly request.ConfirmRetirement)
            token

    let person services (request: PersonSessionTerminationRequest) token =
        terminate
            services
            request.OperationId
            (Some request.ActorId)
            (mode request.SettleOnly request.ConfirmRetirement)
            token
