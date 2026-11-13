namespace ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Loads and persists people work; Domain owns parsing, policy and state decisions.
module internal PeopleOperations =
    let private targets installationId (command: PeopleCommand) (view: PeopleExecutionFacts) =
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
                        CaseOperations.current installationId record
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

    let private domainFacts (view: PeopleExecutionFacts) : PeopleFacts =
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

    let private isProvision (command: PeopleCommand) =
        match command.Act with
        | PeopleAct.ProvisionOwner _ -> true
        | _ -> false

    let private targetId (command: PeopleCommand) (view: PeopleExecutionFacts) =
        match command.Act with
        | PeopleAct.Register _
        | PeopleAct.Admit _
        | PeopleAct.ProvisionOwner _ -> Ok(ActorId.ofOperationId command.OperationId)
        | _ ->
            view.Target.Actor
            |> Option.map (fun actor -> Ok actor.ActorId)
            |> Option.defaultValue (Error(Refusal.People PeopleRefusal.ActorUnknown))

    let private recorded
        (services: ServiceContext)
        (command: PeopleCommand)
        (view: PeopleExecutionFacts)
        =
        if
            not (isProvision command)
            && not (
                Access.can services.Principal view.Actor Capability.ManageAccess Scope.Installation
            )
        then
            Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable)
        else
            match targets services.InstallationId command view with
            | Error fault -> Verdict.Fail fault
            | Ok() ->
                targetId command view
                |> Result.bind (fun id ->
                    PeopleRequests.toRecords
                        (view.TargetCases |> Map.map (fun _ record -> record.Snapshot))
                        id
                        command)
                |> Result.map Verdict.Proceed
                |> Result.defaultWith (fun refusal ->
                    match People.decide (domainFacts view) command with
                    | Error priority -> Verdict.Refuse priority
                    | Ok _ -> Verdict.Refuse refusal)

    let private rootValues (command: PeopleCommand) (records: PeopleEffectRecord list) =
        let grants =
            records
            |> List.choose (fun record ->
                match record.Effect with
                | PeopleEffect.Grant grant
                | PeopleEffect.Revoke grant ->
                    let scope =
                        match grant.Scope with
                        | RecordedGrantScope.Installation -> Scope.Installation
                        | RecordedGrantScope.Case(id, _) -> Scope.Case id

                    Some
                        {
                            ActorGrant.Role = grant.Role
                            Scope = scope
                        }
                | _ -> None)

        let act, profile =
            match command.Act with
            | PeopleAct.ProvisionOwner(profile, roles) ->
                PeopleAct.ProvisionOwner(0, roles), Some profile
            | PeopleAct.Register profile -> PeopleAct.Register 0, Some profile
            | PeopleAct.Admit value ->
                PeopleAct.Admit { Profile = 0; Grants = grants }, Some value.Profile
            | PeopleAct.SetProfile profile -> PeopleAct.SetProfile 0, Some profile
            | PeopleAct.EraseProfile -> PeopleAct.EraseProfile, None
            | PeopleAct.Enable -> PeopleAct.Enable, None
            | PeopleAct.Disable -> PeopleAct.Disable, None
            | PeopleAct.Grant _ -> PeopleAct.Grant(List.exactlyOne grants), None
            | PeopleAct.Revoke _ -> PeopleAct.Revoke(List.exactlyOne grants), None

        {
            PeopleCommandSkeleton.TargetActorId = records.Head.TargetActorId
            Principal = command.Target
            Act = act
        },
        profile

    let private identity command records =
        let scope, profile = rootValues command records
        OperationIntent.people command.OperationId scope profile

    let private authorizeIdentity
        (services: ServiceContext)
        operation
        bytes
        (view: PeopleExecutionFacts)
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
                (ReceiptCurrent.People(view.Target.Actor |> Option.map Actor.currentReference)))

    let private dependencies (view: PeopleExecutionFacts) =
        {
            PeopleSubjectDependencies.Requester = view.Actor
            TargetCases = view.TargetCases |> Map.map (fun _ record -> record.CaseId)
            EnabledHumanOwners = view.EnabledHumanOwners
            ActorCount = view.ActorCount
        }

    let private propose (command: PeopleCommand) (view: PeopleExecutionFacts) =
        let consistent =
            match view.Target.Actor, view.State with
            | None, None -> true
            | Some actor, Some state -> actor = state.Actor
            | _ -> false

        if not consistent then
            Verdict.Fail CoreFault.StoreIntegrityError
        else
            PeopleSubject.decide PeopleAdmissionMode.Current (dependencies view) view.State command
            |> Result.map Verdict.Proceed
            |> Result.defaultWith Verdict.Refuse

    let private provisionIdentity
        (services: ServiceContext)
        operation
        bytes
        (head: LedgerPosition)
        (view: PeopleExecutionFacts)
        =
        if view.ActorCount = Some 0 then
            if head.Sequence = Sequence.zero && view.ExistingOperation.IsNone then
                Verdict.Proceed()
            else
                Verdict.Fail CoreFault.StoreIntegrityError
        else
            match view.ExistingOperation with
            | None -> Verdict.Refuse(Refusal.People PeopleRefusal.ActorAlreadyRegistered)
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
                            (ReceiptCurrent.People(
                                view.Target.Actor |> Option.map Actor.currentReference
                            ))
                            validated
                        |> Result.map Verdict.Replay
                        |> Result.defaultWith Verdict.Fail
                    | _ -> Verdict.Refuse Refusal.IdempotencyConflict

    let private receiptActor
        (command: PeopleCommand)
        (view: PeopleExecutionFacts)
        (effect: PeopleSubjectEffect)
        =
        if isProvision command then
            Some(Actor.currentReference effect.After.Actor)
        else
            view.Actor.Actor
            |> Option.map (fun actor ->
                if actor.Principal = command.Target then
                    Actor.currentReference effect.After.Actor
                else
                    Actor.currentReference actor)

    let decide
        mode
        (services: ServiceContext)
        (command: PeopleCommand)
        (view: PeopleExecutionFacts)
        =
        let admit bytes =
            match mode with
            | SubjectExecutionMode.Commit write when isProvision command ->
                provisionIdentity services command.OperationId bytes write.Head view
            | SubjectExecutionMode.Preview when isProvision command ->
                Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable)
            | _ -> authorizeIdentity services command.OperationId (bytes ()) view

        let window =
            if SubjectExecution.isCommit mode then
                if
                    view.ExistingOperation.IsNone
                    && (isProvision command || Access.validActor services.Principal view.Actor)
                then
                    LedgerEnvelope.lifetime view.ExpiredBefore view.ObservedAt command.OperationId
                else
                    Verdict.Proceed()
            else
                LedgerEnvelope.frontier view.ExpiredBefore view.ObservedAt command.OperationId

        recorded services command view
        |> Verdict.bind (fun records ->
            admit (fun () -> identity command records)
            |> Verdict.bind (fun () -> window)
            |> Verdict.bind (fun () ->
                // Writes retain the final writable-state gate; previews report the pause before current admission.
                if view.Maintenance.Active && not (SubjectExecution.isCommit mode) then
                    Verdict.Refuse Refusal.RegisterMaintenance
                else
                    propose command view)
            |> Verdict.map (fun effect -> records, effect))

    let append
        (_services: ServiceContext)
        (command: PeopleCommand)
        (view: PeopleExecutionFacts)
        (records, effect: PeopleSubjectEffect)
        =
        let actorId =
            if isProvision command then
                effect.After.Actor.ActorId
            else
                LedgerEnvelope.actorId view.Actor

        let scope, profile = rootValues command records

        {
            OperationId = command.OperationId
            ObservedAt = view.ObservedAt
            ActorId = actorId
            RecordedBy = receiptActor command view effect
            Command = CommandSkeleton.People scope
            Values = OperationValues.People { Profile = profile }
            Current = ReceiptCurrent.People(Some(Actor.currentReference effect.After.Actor))
            Projection =
                fun operation ->
                    PeopleSubject.apply (SubjectProvenance.people operation) view.State effect
                    |> Result.toOption
                    |> Option.flatten
                    |> Option.bind PeopleSubject.rows
                    |> Option.defaultWith (fun () ->
                        invalidOp "A decided people operation has complete rows.")
        }

    let registration (services: ServiceContext) =
        {
            LoadAndDecide =
                fun mode tx (command: PeopleCommand) ->
                    task {
                        let! view =
                            ExecutionFacts.target
                                services
                                tx
                                (People.factsNeeded command.Act)
                                command.OperationId
                                command.Target
                                (PeopleRequests.targetCases command)

                        return view, decide mode services command view
                    }
            Actor = fun view -> view.Actor
            Append = fun command view decision -> append services command view decision
            Project = fun tx rows -> tx.ProjectPeople rows
            Review =
                fun _ view (_, effect) ->
                    PeopleReviews.describe services view effect |> OperationReview.People
        }

    let provisionInTransaction (services: ServiceContext) tx operation profile roles =
        match People.provision operation services.Principal profile roles with
        | Error refusal -> System.Threading.Tasks.Task.FromResult(Verdict.Refuse refusal)
        | Ok command -> SubjectExecution.inTransaction services (registration services) command tx

    let provisionStore (services: ServiceContext) operation profile roles token =
        match People.provision operation services.Principal profile roles with
        | Error refusal -> System.Threading.Tasks.Task.FromResult(Ok(Verdict.Refuse refusal))
        | Ok command -> SubjectExecution.run services (registration services) command token
