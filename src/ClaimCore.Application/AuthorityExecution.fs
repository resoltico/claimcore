namespace ClaimCore.Application

open System
open ClaimCore.Contract
open ClaimCore.Domain
open ClaimCore.RecordFormat

/// Loads and persists authority work; Domain owns parsing, policy and state decisions.
module internal AuthorityExecution =
    let private targets installationId (command: AuthorityCommand) (view: AuthorityExecutionFacts) =
        if
            view.Target.Actor
            |> Option.exists (fun actor ->
                actor.ActorId = Guid.Empty || actor.Principal <> command.Target)
        then
            Error CoreFault.StoreIntegrityError
        else
            match command.Act, view.TargetCase with
            | AuthorityCommandAct.Grant(_, GrantTarget.CaseReference reference), Some record
            | AuthorityCommandAct.Revoke(_, GrantTarget.CaseReference reference), Some record ->
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
                    ActorId = actor.ActorId
                    Principal = actor.Principal
                    Enabled = actor.Enabled
                    Grants =
                        (view.Target.Installation
                         |> List.map (fun role ->
                             {
                                 Role = role
                                 Scope = GrantScope.Installation
                             }))
                        @ (view.Target.Cases
                           |> Map.toList
                           |> List.collect (fun (id, roles) ->
                               roles
                               |> List.map (fun role ->
                                   {
                                       Role = role
                                       Scope = GrantScope.Case id
                                   })))
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
            | GrantScope.Installation -> None
            | GrantScope.Case id -> Some id

        match change, view.Target.Actor with
        | AuthorityChange.Registered, _ ->
            Ok(
                command.OperationId,
                AuthorityProjection.Register(command.OperationId, command.Target)
            )
        | AuthorityChange.OwnerProvisioned, _ ->
            Ok(
                command.OperationId,
                AuthorityProjection.ProvisionOwner(command.OperationId, command.Target)
            )
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
        installationId
        principal
        operationId
        bytes
        (view: AuthorityExecutionFacts)
        =
        if
            not (
                Authority.can
                    principal
                    view.Actor
                    Capability.ManageAuthority
                    ResourceScope.Installation
            )
        then
            Verdict.Refuse Refusal.ResourceUnavailable
        else
            OperationIdentity.decide
                installationId
                principal
                view.Actor
                operationId
                bytes
                view.ExistingOperation
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
        installationId
        operationId
        principal
        bytes
        (head: LedgerHead)
        (view: AuthorityExecutionFacts)
        =
        if view.ActorCount = 0 then
            if head.Sequence = 0L && view.ExistingOperation.IsNone then
                Verdict.Proceed()
            else
                Verdict.Fail CoreFault.StoreIntegrityError
        else
            match view.ExistingOperation with
            | None -> Verdict.Refuse Refusal.ActorAlreadyRegistered
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
            ObservedAt = view.ObservedAt
            ActorId =
                match command.Act with
                | AuthorityCommandAct.ProvisionOwner -> targetId
                | _ -> LedgerEnvelope.actorId view.Actor
            Subject = EntryTarget.Authority(targetId, RecordedAuthorityActs.kind record.Act)
            Snapshot = Array.empty
            Current = None
            Projection = projection
        }

    let review
        installationId
        principal
        (command: AuthorityCommand)
        (view: AuthorityExecutionFacts)
        =
        let record = AuthorityRequests.toRecord command
        let bytes = AuthorityRequests.canonical record |> AuthorityRecord.encode

        authorizeIdentity installationId principal command.OperationId bytes view
        |> Verdict.bind (fun () -> propose installationId command view)

    let private executeRecord
        installationId
        principal
        (command: AuthorityCommand)
        admit
        head
        fenced
        nonce
        (view: AuthorityExecutionFacts)
        =
        let record = AuthorityRequests.toRecord command
        let bytes = AuthorityRequests.canonical record |> AuthorityRecord.encode

        admit bytes
        |> Verdict.bind (fun () -> LedgerEnvelope.guard fenced)
        |> Verdict.bind (fun () -> propose installationId command view)
        |> Verdict.bind (fun proposed ->
            let plan = project command record view proposed

            LedgerEnvelope.accept
                installationId
                principal
                view.Actor
                command.OperationId
                head
                nonce
                plan
                (AuthorityRecord.encode record))

    let execute
        installationId
        principal
        (command: AuthorityCommand)
        head
        fenced
        nonce
        (view: AuthorityExecutionFacts)
        =
        executeRecord
            installationId
            principal
            command
            (fun bytes -> authorizeIdentity installationId principal command.OperationId bytes view)
            head
            fenced
            nonce
            view

    let provision
        installationId
        operationId
        target
        head
        fenced
        nonce
        (view: AuthorityExecutionFacts)
        =
        match Authority.provision operationId target with
        | Error refusal -> Verdict.Refuse refusal
        | Ok command ->
            executeRecord
                installationId
                target
                command
                (fun bytes -> provisionIdentity installationId operationId target bytes head view)
                head
                fenced
                nonce
                view

    let run (store: IStore) installationId principal (command: AuthorityCommand) token =
        store.Write(
            (fun tx ->
                task {
                    let! view =
                        ExecutionFacts.target
                            tx
                            installationId
                            principal
                            command.OperationId
                            command.Target
                            (AuthorityRequests.targetCase command)

                    let! fenced = ExecutionFacts.fenced tx view.Actor command.OperationId

                    return!
                        execute installationId principal command tx.Head fenced tx.Nonce view
                        |> ExecutionFacts.persist tx tx.ProjectAuthority
                }),
            token
        )

    let provisionStore (store: IStore) installationId operationId target token =
        store.Write(
            (fun tx ->
                task {
                    let! view =
                        ExecutionFacts.target tx installationId target operationId target None

                    let! fenced = ExecutionFacts.fenced tx view.Actor operationId

                    return!
                        provision installationId operationId target tx.Head fenced tx.Nonce view
                        |> ExecutionFacts.persist tx tx.ProjectAuthority
                }),
            token
        )
