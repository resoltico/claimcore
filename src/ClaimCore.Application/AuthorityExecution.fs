namespace ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.RecordFormat

/// Loads and persists authority work; Domain owns parsing, policy and state decisions.
module internal AuthorityExecution =
    let private targets installationId (command: AuthorityCommand) (view: AuthorityExecutionFacts) =
        if
            view.Target.Actor
            |> Option.exists (fun actor -> actor.Principal <> command.Target)
        then
            Error CoreFault.StoreIntegrityError
        else
            match command.Act, view.TargetCase with
            | AuthorityAct.Grant {
                                     Role = _
                                     Scope = GrantTarget.CaseReference reference
                                 },
              Some record
            | AuthorityAct.Revoke {
                                      Role = _
                                      Scope = GrantTarget.CaseReference reference
                                  },
              Some record ->
                CaseExecution.current installationId record
                |> Result.bind (fun _ ->
                    if
                        CaseReference.identity record.Snapshot.Reference =
                            CaseReference.key reference
                    then
                        Ok()
                    else
                        Error CoreFault.StoreIntegrityError)
            | _ -> Ok()

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
            TargetCaseId = view.TargetCase |> Option.map _.CaseId
            EnabledHumanOwners = view.EnabledHumanOwners
            ActorCount = view.ActorCount
            Requester = view.Actor
        }

    let private projection (command: AuthorityCommand) (view: AuthorityExecutionFacts) change =
        let scope =
            function
            | Scope.Installation -> None
            | Scope.Case id -> Some id

        let registeredId = ActorId.ofOperationId command.OperationId

        match change, view.Target.Actor with
        | AuthorityChange.Registered, _ ->
            Ok(registeredId, AuthorityProjection.Register(registeredId, command.Target))
        | AuthorityChange.OwnerProvisioned, _ ->
            Ok(registeredId, AuthorityProjection.ProvisionOwner(registeredId, command.Target))
        | AuthorityChange.Granted(role, target), Some actor ->
            Ok(actor.ActorId, AuthorityProjection.AddGrant(actor.ActorId, role, scope target))
        | AuthorityChange.Revoked(role, target), Some actor ->
            Ok(actor.ActorId, AuthorityProjection.RemoveGrant(actor.ActorId, role, scope target))
        | AuthorityChange.Enabled, Some actor ->
            Ok(actor.ActorId, AuthorityProjection.SetEnabled(actor.ActorId, true))
        | AuthorityChange.Disabled, Some actor ->
            Ok(actor.ActorId, AuthorityProjection.SetEnabled(actor.ActorId, false))
        | _, None -> Error CoreFault.StoreIntegrityError

    let private authorizeIdentity
        (services: ServiceContext)
        operationId
        bytes
        (view: AuthorityExecutionFacts)
        =
        let principal = services.Principal

        if
            not (Authority.can principal view.Actor Capability.ManageAuthority Scope.Installation)
        then
            Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable)
        else
            OperationIdentity.decide
                services
                view.Actor
                {
                    OperationId = operationId
                    Bytes = bytes
                    Existing = view.ExistingOperation
                }
                (LedgerEntries.receipt principal view.Actor None)


    let private propose installationId command view =
        match targets installationId command view with
        | Error fault -> Verdict.Fail fault
        | Ok() ->
            match Authority.decide (domainFacts view) command with
            | Error refusal -> Verdict.Refuse refusal
            | Ok change ->
                projection command view change
                |> Result.map Verdict.Proceed
                |> Result.defaultWith Verdict.Fail

    let private provisionIdentity
        (services: ServiceContext)
        operationId
        bytes
        (head: LedgerPosition)
        (view: AuthorityExecutionFacts)
        =
        let installationId = services.InstallationId
        let principal = services.Principal

        if view.ActorCount = 0 then
            if head.Sequence = Sequence.zero && view.ExistingOperation.IsNone then
                Verdict.Proceed()
            else
                Verdict.Fail CoreFault.StoreIntegrityError
        else
            match view.ExistingOperation with
            | None -> Verdict.Refuse(Refusal.Authority AuthorityRefusal.ActorAlreadyRegistered)
            | Some entry when entry.OperationId <> operationId ->
                Verdict.Fail CoreFault.StoreIntegrityError
            | Some entry ->
                match LedgerEntries.validate installationId entry with
                | Error fault -> Verdict.Fail fault
                | Ok validated ->
                    match LedgerEntries.identityBytes validated with
                    | Some accepted when accepted = bytes ->
                        LedgerEntries.receipt principal view.Actor None validated
                        |> Result.map Verdict.Replay
                        |> Result.defaultWith Verdict.Fail
                    | _ -> Verdict.Refuse Refusal.IdempotencyConflict

    let private project
        (command: AuthorityCommand)
        (record: AuthorityRequestRecord)
        (view: AuthorityExecutionFacts)
        (targetId, projection)
        =
        {
            OperationId = command.OperationId
            ObservedAt = view.ObservedAt
            ActorId =
                match command.Act with
                | AuthorityAct.ProvisionOwner -> targetId
                | _ -> LedgerEnvelope.actorId view.Actor
            Subject = EntryTarget.Authority(targetId, AuthorityAct.kind record.Act)
            Snapshot = Array.empty
            Current = None
            Projection = projection
        }

    let review
        (services: ServiceContext)
        (command: AuthorityCommand)
        (view: AuthorityExecutionFacts)
        =
        let installationId = services.InstallationId
        let record = AuthorityRequests.toRecord command
        let bytes = AuthorityRequests.canonical record |> AuthorityRecord.encode

        authorizeIdentity services command.OperationId bytes view
        |> Verdict.bind (fun () -> propose installationId command view)

    let private executeRecord
        (services: ServiceContext)
        (command: AuthorityCommand)
        admit
        (write: LedgerWriteContext)
        (view: AuthorityExecutionFacts)
        =
        let installationId = services.InstallationId
        let record = AuthorityRequests.toRecord command
        let bytes = AuthorityRequests.canonical record |> AuthorityRecord.encode

        admit bytes
        |> Verdict.bind (fun () -> LedgerEnvelope.guard write.Fenced)
        |> Verdict.bind (fun () -> propose installationId command view)
        |> Verdict.bind (fun proposed ->
            let plan = project command record view proposed

            LedgerEnvelope.accept services view.Actor write plan (AuthorityRecord.encode record))

    let execute
        (services: ServiceContext)
        (command: AuthorityCommand)
        (write: LedgerWriteContext)
        (view: AuthorityExecutionFacts)
        =
        executeRecord
            services
            command
            (fun bytes -> authorizeIdentity services command.OperationId bytes view)
            write
            view

    let provision
        (services: ServiceContext)
        operationId
        (write: LedgerWriteContext)
        (view: AuthorityExecutionFacts)
        =
        let target = services.Principal

        match Authority.provision (OperationId.value operationId) target with
        | Error refusal -> Verdict.Refuse refusal
        | Ok command ->
            executeRecord
                services
                command
                (fun bytes -> provisionIdentity services operationId bytes write.Head view)
                write
                view

    let run (services: ServiceContext) (command: AuthorityCommand) token =
        let store = services.Store

        store.Write(
            {
                WriteOperation.Work =
                    (fun tx ->
                        task {
                            let! view =
                                ExecutionFacts.target
                                    services
                                    tx
                                    command.OperationId
                                    command.Target
                                    (AuthorityRequests.targetCase command)

                            let! fenced = ExecutionFacts.fenced tx view.Actor command.OperationId

                            return!
                                execute
                                    services
                                    command
                                    {
                                        Head = tx.Head
                                        Fenced = fenced
                                        Nonce = tx.Nonce
                                    }
                                    view
                                |> ExecutionFacts.persist tx tx.ProjectAuthority
                        })
                CancellationToken = token
            }
        )

    let provisionStore (services: ServiceContext) operationId token =
        let store = services.Store
        let target = services.Principal

        let operationId =
            OperationId.create operationId
            |> Option.defaultWith (fun () ->
                invalidArg (nameof operationId) "Use a nonempty provisioning operation.")

        store.Write(
            {
                WriteOperation.Work =
                    (fun tx ->
                        task {
                            let! view = ExecutionFacts.target services tx operationId target None

                            let! fenced = ExecutionFacts.fenced tx view.Actor operationId

                            return!
                                provision
                                    services
                                    operationId
                                    {
                                        Head = tx.Head
                                        Fenced = fenced
                                        Nonce = tx.Nonce
                                    }
                                    view
                                |> ExecutionFacts.persist tx tx.ProjectAuthority
                        })
                CancellationToken = token
            }
        )
