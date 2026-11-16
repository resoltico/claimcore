namespace ClaimCore.Application.Execution

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Tasks
open ClaimCore.Application.Storage

/// Live facts are acquired inside the snapshot used for the action, independently of prior offers.
module internal ExecutionEnvironment =
    let fromStorage (tx: ReadTx) principal maintenance selected binding inputs =
        task {
            let! requester = AggregateStorage.principal tx principal
            let access = requester |> Option.map Person.access |> AccessView.ofActor

            match selected with
            | Task.Family selected ->
                let target =
                    Binding.caseTarget binding
                    |> Option.orElseWith (fun () ->
                        InputSelection.target
                            (InputTarget.Field BusinessField.CaseReference)
                            inputs
                        |> Option.map CaseSelector.Reference)

                let! family =
                    task {
                        match target with
                        | None -> return None
                        | Some target ->
                            let! id = AggregateStorage.resolveCase tx target

                            match id with
                            | None -> return None
                            | Some id when
                                Access.can principal access Capability.ReadCase (Scope.Case id)
                                ->
                                return! AggregateStorage.family tx id
                            | Some _ -> return None
                    }

                let! responsible =
                    task {
                        match family, InputSelection.responsible inputs with
                        | Some(family, _, _), Some id when
                            Access.can
                                principal
                                access
                                Capability.ManageCaseWork
                                (Scope.Case family.Id)
                            ->
                            return! AggregateStorage.person tx id
                        | _ -> return None
                    }

                let environment =
                    {
                        FamilyEnvironment.Requester = access
                        RequesterPrincipal = principal
                        Maintenance = maintenance
                        AssigneeEligible =
                            fun id ->
                                match family, responsible with
                                | Some(family, _, _), Some person when person.Id = id ->
                                    let view = Person.access person |> Some |> AccessView.ofActor

                                    Access.eligibleAssignee person.Actor.Principal view family.Id
                                | _ -> false
                    }

                let state, parts, origins =
                    match family with
                    | Some(state, parts, origins) -> Some state, parts, origins
                    | None -> None, [], Map.empty

                return DecisionSnapshot.family selected environment state parts origins
            | Task.Person selected ->
                let! person =
                    task {
                        match binding with
                        | _ when
                            not (
                                Access.can principal access Capability.ReadAccess Scope.Installation
                            )
                            ->
                            return None
                        | Binding.People(id, _) -> return! AggregateStorage.person tx id
                        | _ ->
                            match InputSelection.principal inputs with
                            | None -> return None
                            | Some principal -> return! AggregateStorage.principal tx principal
                    }

                let mutable cases = Map.empty

                for target in InputSelection.grantTargets inputs do
                    let selector =
                        match target with
                        | GrantTarget.CaseId id -> Some(CaseSelector.Id id)
                        | GrantTarget.CaseReference reference ->
                            Some(CaseSelector.Reference reference)
                        | GrantTarget.Installation -> None

                    match selector with
                    | None -> ()
                    | Some selector ->
                        let! id = AggregateStorage.resolveCase tx selector

                        match id with
                        | Some id -> cases <- Map.add (GrantTarget.canonical target) id cases
                        | None -> ()

                let! counts =
                    DatabaseSession.rows
                        tx.Connection
                        "SELECT (SELECT count(*)::integer FROM claimcore.actors),(SELECT count(*)::integer FROM claimcore.actors a JOIN claimcore.grants g USING(actor_id) WHERE a.enabled AND a.principal_kind=$1 AND g.role=$2 AND g.case_id IS NULL)"
                        [|
                            box (Vocabulary.token PrincipalKind.vocabulary PrincipalKind.Human)
                            box (Role.token Role.Owner)
                        |]
                        tx.CancellationToken

                let environment =
                    {
                        PersonEnvironment.Requester = access
                        RequesterPrincipal = principal
                        Maintenance = maintenance
                        TargetCases = cases
                        ActorCount = DatabaseSession.value<int> 0 counts.Head
                        EnabledHumanOwners = DatabaseSession.value<int> 1 counts.Head
                        TargetPrincipal =
                            person
                            |> Option.map _.Actor.Principal
                            |> Option.orElseWith (fun () -> InputSelection.principal inputs)
                    }

                return DecisionSnapshot.person selected environment person
        }
