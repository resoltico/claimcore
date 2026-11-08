namespace ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Loads and persists authority work; Domain owns parsing, policy and state decisions.
module internal AuthorityExecution =
    let private targets installationId (command: AuthorityCommand) (view: AuthorityExecutionFacts) =
        let rolesValid =
            let ids = view.Target.Actor |> Option.map _.ActorId |> Option.toList |> Set.ofList

            (view.TargetRoles |> Map.keys |> Set.ofSeq) = ids
            && (view.TargetRoles |> Map.forall (fun _ roles -> ActorRoleSummary.valid roles))

        if
            not rolesValid
            || (view.Target.Actor
                |> Option.exists (fun actor -> actor.Principal <> command.Target))
        then
            Error CoreFault.StoreIntegrityError
        else
            view.TargetCases
            |> Map.toList
            |> List.fold
                (fun result (target, record) ->
                    result
                    |> Result.bind (fun () ->
                        CaseExecution.current installationId record
                        |> Result.bind (fun _ ->
                            let matches =
                                match target with
                                | GrantTarget.CaseReference reference ->
                                    CaseReference.identity record.Snapshot.Reference =
                                        CaseReference.key reference
                                | GrantTarget.CaseId id -> record.CaseId = id
                                | GrantTarget.Installation -> false

                            if matches then
                                Ok()
                            else
                                Error CoreFault.StoreIntegrityError)))
                (Ok())

    let private domainFacts (view: AuthorityExecutionFacts) : AuthorityFacts =
        let target =
            view.Target.Actor
            |> Option.map (fun actor ->
                {
                    Actor =
                        {
                            ActorId = actor.ActorId
                            Principal = actor.Principal
                            Enabled = actor.Enabled
                            Profile = actor.Profile
                        }
                    Grants =
                        (view.Target.Installation
                         |> List.map (fun role ->
                             {
                                 Role = role
                                 Scope = Scope.Installation
                             }))
                        @ (view.Target.Cases
                           |> Map.toList
                           |> List.collect (fun (id, roles) ->
                               roles
                               |> List.map (fun role -> { Role = role; Scope = Scope.Case id })))
                })

        {
            TargetActor = target
            TargetCases = view.TargetCases |> Map.map (fun _ record -> record.CaseId)
            EnabledHumanOwners = view.EnabledHumanOwners
            ActorCount = view.ActorCount
            Requester = view.Actor
        }

    let private isProvision (command: AuthorityCommand) =
        match command.Act with
        | AuthorityAct.ProvisionOwner _ -> true
        | _ -> false

    let private targetId (command: AuthorityCommand) (view: AuthorityExecutionFacts) =
        match command.Act with
        | AuthorityAct.Register _
        | AuthorityAct.Admit _
        | AuthorityAct.ProvisionOwner _ -> Ok(ActorId.ofOperationId command.OperationId)
        | _ ->
            view.Target.Actor
            |> Option.map (fun actor -> Ok actor.ActorId)
            |> Option.defaultValue (Error(Refusal.Authority AuthorityRefusal.ActorUnknown))

    let private recorded
        (services: ServiceContext)
        (command: AuthorityCommand)
        (view: AuthorityExecutionFacts)
        =
        if
            not (isProvision command)
            && not (
                Authority.can
                    services.Principal
                    view.Actor
                    Capability.ManageAuthority
                    Scope.Installation
            )
        then
            Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable)
        else
            match targets services.InstallationId command view with
            | Error fault -> Verdict.Fail fault
            | Ok() ->
                targetId command view
                |> Result.bind (fun id ->
                    AuthorityRequests.toRecords
                        (view.TargetCases |> Map.map (fun _ record -> record.Snapshot))
                        id
                        command)
                |> Result.map Verdict.Proceed
                |> Result.defaultWith (fun refusal ->
                    match Authority.decide (domainFacts view) command with
                    | Error priority -> Verdict.Refuse priority
                    | Ok _ -> Verdict.Refuse refusal)

    let private identity (command: AuthorityCommand) (records: AuthorityEffectRecord list) =
        records
        |> List.map (AuthorityRequests.canonical >> AuthorityRecord.encode)
        |> LedgerEntries.authorityIdentityBytes (AuthorityAct.kind command.Act)

    let private authorizeIdentity
        (services: ServiceContext)
        operation
        bytes
        (view: AuthorityExecutionFacts)
        =
        OperationIdentity.decide
            services
            view.Actor
            {
                OperationId = operation
                Bytes = bytes
                Existing = view.ExistingOperation
            }
            (LedgerEntries.receiptOperation
                (ServiceContext.basis services view.ObservedAt)
                services.Principal
                view.Actor
                (view.Actor.Actor |> Option.map Actor.currentReference)
                (ReceiptCurrent.Authority(view.Target.Actor |> Option.map Actor.currentReference)))

    let private propose (command: AuthorityCommand) (view: AuthorityExecutionFacts) =
        match Authority.decide (domainFacts view) command, targetId command view with
        | Error refusal, _
        | _, Error refusal -> Verdict.Refuse refusal
        | Ok change, Ok target ->
            Verdict.Proceed
                {
                    AuthorityProposal.TargetActorId = target
                    Principal = command.Target
                    Change = change
                }

    let private provisionIdentity
        (services: ServiceContext)
        operation
        bytes
        (head: LedgerPosition)
        (view: AuthorityExecutionFacts)
        =
        if view.ActorCount = Some 0 then
            if head.Sequence = Sequence.zero && view.ExistingOperation.IsNone then
                Verdict.Proceed()
            else
                Verdict.Fail CoreFault.StoreIntegrityError
        else
            match view.ExistingOperation with
            | None -> Verdict.Refuse(Refusal.Authority AuthorityRefusal.ActorAlreadyRegistered)
            | Some existing when existing.OperationId <> operation ->
                Verdict.Fail CoreFault.StoreIntegrityError
            | Some existing ->
                match LedgerEntries.validateOperation services.InstallationId existing with
                | Error fault -> Verdict.Fail fault
                | Ok validated ->
                    match LedgerEntries.identityBytesOperation validated with
                    | None -> Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable)
                    | Some _ when not (PrincipalKey.isHuman services.Principal) ->
                        Verdict.Refuse Refusal.IdempotencyConflict
                    | Some accepted when accepted = bytes () ->
                        LedgerEntries.receiptOperation
                            (ServiceContext.basis services view.ObservedAt)
                            services.Principal
                            view.Actor
                            (view.Actor.Actor |> Option.map Actor.currentReference)
                            (ReceiptCurrent.Authority(
                                view.Target.Actor |> Option.map Actor.currentReference
                            ))
                            validated
                        |> Result.map Verdict.Replay
                        |> Result.defaultWith Verdict.Fail
                    | _ -> Verdict.Refuse Refusal.IdempotencyConflict

    let private projection
        (command: AuthorityCommand)
        (entry: EntryDraft)
        (record: AuthorityEffectRecord)
        =
        let id = record.TargetActorId

        let scope (grant: RecordedGrant) =
            match grant.Scope with
            | RecordedGrantScope.Installation -> None
            | RecordedGrantScope.Case(id, _) -> Some id

        match record.Effect with
        | AuthorityEffect.Register _ -> AuthorityProjection.Register(id, command.Target)
        | AuthorityEffect.ProvisionOwner _ -> AuthorityProjection.ProvisionOwner(id, command.Target)
        | AuthorityEffect.SetProfile profile -> AuthorityProjection.SetProfile(id, profile)
        | AuthorityEffect.EraseProfile -> AuthorityProjection.EraseProfile(id, entry.Sequence)
        | AuthorityEffect.Enable -> AuthorityProjection.SetEnabled(id, true)
        | AuthorityEffect.Disable -> AuthorityProjection.SetEnabled(id, false)
        | AuthorityEffect.Grant grant -> AuthorityProjection.AddGrant(id, grant.Role, scope grant)
        | AuthorityEffect.Revoke grant ->
            AuthorityProjection.RemoveGrant(id, grant.Role, scope grant)

    let review
        (services: ServiceContext)
        (command: AuthorityCommand)
        (view: AuthorityExecutionFacts)
        =
        recorded services command view
        |> Verdict.bind (fun records ->
            authorizeIdentity services command.OperationId (identity command records) view
            |> Verdict.bind (fun () ->
                if view.Maintenance.Active then
                    Verdict.Refuse Refusal.RegisterMaintenance
                else
                    propose command view))

    let private afterActor act (actor: Actor) =
        match act with
        | AuthorityAct.SetProfile profile -> { actor with Profile = Some profile }
        | AuthorityAct.EraseProfile -> { actor with Profile = None }
        | AuthorityAct.Disable -> { actor with Enabled = false }
        | AuthorityAct.Enable -> { actor with Enabled = true }
        | _ -> actor

    let private targetActor (command: AuthorityCommand) (view: AuthorityExecutionFacts) =
        let created profile =
            let kind, _, _ = PrincipalKey.parts command.Target

            Some(
                ActorRef.create
                    (ActorId.ofOperationId command.OperationId)
                    kind
                    ActorAvailability.Available
                    (Some profile)
            )

        match command.Act with
        | AuthorityAct.Register profile
        | AuthorityAct.ProvisionOwner profile -> created profile
        | AuthorityAct.Admit admission -> created admission.Profile
        | _ ->
            view.Target.Actor
            |> Option.map (afterActor command.Act >> Actor.currentReference)

    let private receiptActor (command: AuthorityCommand) (view: AuthorityExecutionFacts) =
        match command.Act with
        | AuthorityAct.ProvisionOwner _ -> targetActor command view
        | _ ->
            view.Actor.Actor
            |> Option.map (fun actor ->
                (if actor.Principal = command.Target then
                     afterActor command.Act actor
                 else
                     actor)
                |> Actor.currentReference)

    let private executeRecord
        (services: ServiceContext)
        (command: AuthorityCommand)
        admit
        (write: AuthorityWriteContext)
        (view: AuthorityExecutionFacts)
        =
        let lifetime =
            if
                view.ExistingOperation.IsNone
                && (isProvision command || Authority.validActor services.Principal view.Actor)
            then
                LedgerEnvelope.lifetime view.ObservedAt command.OperationId
            else
                Verdict.Proceed()

        lifetime
        |> Verdict.bind (fun () -> recorded services command view)
        |> Verdict.bind (fun records ->
            admit (fun () -> identity command records)
            |> Verdict.bind (fun () -> propose command view)
            |> Verdict.bind (fun _ ->
                let actorId =
                    if isProvision command then
                        ActorId.ofOperationId command.OperationId
                    else
                        LedgerEnvelope.actorId view.Actor

                LedgerEntries.createAuthorityOperation
                    (AuthorityAct.kind command.Act)
                    write.NewPayloadNonce
                    {
                        InstallationId = services.InstallationId
                        Head = write.Head
                        Instant = view.ObservedAt
                        ActorId = actorId
                        OperationId = command.OperationId
                        Effects =
                            records
                            |> List.map (fun record ->
                                {
                                    Subject =
                                        EntryTarget.Authority(
                                            record.TargetActorId,
                                            AuthorityEffect.kind record.Effect
                                        )
                                    Request = AuthorityRecord.encode record
                                    Snapshot = Array.empty
                                })
                    }
                |> Result.bind (fun entries ->
                    entries
                    |> List.map LedgerEntries.stored
                    |> StoredOperation.create
                    |> Result.bind (LedgerEntries.validateOperation services.InstallationId)
                    |> Result.bind (
                        LedgerEntries.receiptOperation
                            (ServiceContext.basis services view.ObservedAt)
                            services.Principal
                            view.Actor
                            (receiptActor command view)
                            (ReceiptCurrent.Authority(targetActor command view))
                    )
                    |> Result.map (fun receipt ->
                        entries, List.map2 (projection command) entries records, receipt))
                |> Result.map Verdict.Proceed
                |> Result.defaultWith Verdict.Fail))

    let execute
        (services: ServiceContext)
        (command: AuthorityCommand)
        (write: AuthorityWriteContext)
        (view: AuthorityExecutionFacts)
        =
        executeRecord
            services
            command
            (fun bytes -> authorizeIdentity services command.OperationId (bytes ()) view)
            write
            view

    let provision
        (services: ServiceContext)
        operation
        profile
        (write: AuthorityWriteContext)
        (view: AuthorityExecutionFacts)
        =
        match Authority.provision (OperationId.value operation) services.Principal profile with
        | Error refusal -> Verdict.Refuse refusal
        | Ok command ->
            executeRecord
                services
                command
                (fun bytes -> provisionIdentity services operation bytes write.Head view)
                write
                view

    let run (services: ServiceContext) (command: AuthorityCommand) token =
        services.Store.Write
            {
                Work =
                    (fun tx ->
                        task {
                            let! view =
                                ExecutionFacts.target
                                    services
                                    tx
                                    (Authority.factsNeeded command.Act)
                                    command.OperationId
                                    command.Target
                                    (AuthorityRequests.targetCases command)

                            let verdict =
                                execute
                                    services
                                    command
                                    {
                                        Head = tx.Head
                                        NewPayloadNonce = tx.NewPayloadNonce
                                    }
                                    view

                            return! ExecutionFacts.persistOperation tx tx.ProjectAuthority verdict
                        })
                CancellationToken = token
            }

    let provisionStore (services: ServiceContext) operation profile token =
        match Authority.provision operation services.Principal profile with
        | Error refusal -> System.Threading.Tasks.Task.FromResult(Ok(Verdict.Refuse refusal))
        | Ok command ->
            services.Store.Write
                {
                    Work =
                        (fun tx ->
                            task {
                                let! view =
                                    ExecutionFacts.target
                                        services
                                        tx
                                        (Authority.factsNeeded command.Act)
                                        command.OperationId
                                        command.Target
                                        []

                                let write =
                                    {
                                        AuthorityWriteContext.Head = tx.Head
                                        NewPayloadNonce = tx.NewPayloadNonce
                                    }

                                let verdict =
                                    executeRecord
                                        services
                                        command
                                        (fun bytes ->
                                            provisionIdentity
                                                services
                                                command.OperationId
                                                bytes
                                                tx.Head
                                                view)
                                        write
                                        view

                                return!
                                    ExecutionFacts.persistOperation tx tx.ProjectAuthority verdict
                            })
                    CancellationToken = token
                }
