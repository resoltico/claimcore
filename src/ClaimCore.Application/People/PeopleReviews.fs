namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Reviews describe admitted domain changes, independently of physical effect sequencing.
module internal PeopleReviews =
    let describe services (view: PeopleExecutionFacts) (proposal: PeopleSubjectEffect) =
        let profile = view.Target.Actor |> Option.bind _.Profile

        let before: PeopleState =
            {
                Enabled = view.Target.Actor |> Option.exists _.Enabled
                Profile = profile
                Grants = []
                Access = []
            }

        let scopeView =
            function
            | Scope.Installation -> GrantScope.Installation
            | Scope.Case id ->
                view.TargetCases
                |> Map.values
                |> Seq.tryFind (fun record -> record.CaseId = id)
                |> Option.map (fun record -> GrantScope.Case(id, record.Snapshot.Reference))
                |> Option.defaultWith (fun () ->
                    invalidOp "A scoped people proposal requires its case.")

        let grantView (value: Grant<Scope>) : ScopedGrant =
            {
                Role = value.Role
                Scope = scopeView value.Scope
            }

        let effects =
            match proposal.Change with
            | PeopleChange.Registered profile ->
                [
                    PeopleReviewEffect.Register proposal.After.Actor.Principal
                    PeopleReviewEffect.SetProfile profile
                ]
            | PeopleChange.OwnerProvisioned(profile, roles) ->
                [
                    PeopleReviewEffect.ProvisionOwner proposal.After.Actor.Principal
                    PeopleReviewEffect.SetProfile profile
                    for role in roles do
                        PeopleReviewEffect.Grant
                            {
                                Role = role
                                Scope = GrantScope.Installation
                            }
                ]
            | PeopleChange.Admitted(profile, grants) ->
                PeopleReviewEffect.Register proposal.After.Actor.Principal
                :: PeopleReviewEffect.SetProfile profile
                :: (grants |> List.map (grantView >> PeopleReviewEffect.Grant))
            | PeopleChange.ProfileSet profile -> [ PeopleReviewEffect.SetProfile profile ]
            | PeopleChange.ProfileErased -> [ PeopleReviewEffect.EraseProfile ]
            | PeopleChange.Enabled -> [ PeopleReviewEffect.Enable ]
            | PeopleChange.Disabled -> [ PeopleReviewEffect.Disable ]
            | PeopleChange.Granted(role, scope) ->
                [ PeopleReviewEffect.Grant(grantView { Role = role; Scope = scope }) ]
            | PeopleChange.Revoked(role, scope) ->
                [ PeopleReviewEffect.Revoke(grantView { Role = role; Scope = scope }) ]

        let resource, scope =
            match proposal.Change with
            | PeopleChange.Granted(_, Scope.Case id)
            | PeopleChange.Revoked(_, Scope.Case id) -> Scope.Case id, scopeView (Scope.Case id)
            | _ -> Scope.Installation, GrantScope.Installation

        let before, after =
            match proposal.Change with
            | PeopleChange.Registered profile ->
                before,
                { before with
                    Enabled = true
                    Profile = Some profile
                }
            | PeopleChange.OwnerProvisioned(profile, roles) ->
                before,
                { before with
                    Enabled = true
                    Profile = Some profile
                    Grants =
                        [
                            {
                                Role = Role.Owner
                                Scope = GrantScope.Installation
                            }
                            for role in roles do
                                {
                                    Role = role
                                    Scope = GrantScope.Installation
                                }
                        ]
                }
            | PeopleChange.Admitted(profile, grants) ->
                before,
                { before with
                    Enabled = true
                    Profile = Some profile
                    Grants = List.map grantView grants
                }
            | PeopleChange.ProfileSet profile -> before, { before with Profile = Some profile }
            | PeopleChange.ProfileErased -> before, { before with Profile = None }
            | PeopleChange.Enabled -> before, { before with Enabled = true }
            | PeopleChange.Disabled -> before, { before with Enabled = false }
            | PeopleChange.Granted(role, _)
            | PeopleChange.Revoked(role, _) ->
                let roles =
                    match resource with
                    | Scope.Installation -> view.Target.Installation
                    | Scope.Case id -> Map.tryFind id view.Target.Cases |> Option.defaultValue []

                let grant =
                    {
                        ScopedGrant.Role = role
                        Scope = scope
                    }

                let before =
                    { before with
                        Grants = (if List.contains role roles then [ grant ] else [])
                    }

                let grants =
                    match proposal.Change with
                    | PeopleChange.Granted _ -> [ grant ]
                    | _ -> []

                before, { before with Grants = grants }

        let proposed =
            match proposal.Change with
            | PeopleChange.Registered profile
            | PeopleChange.OwnerProvisioned(profile, _) ->
                let grants =
                    match proposal.Change with
                    | PeopleChange.OwnerProvisioned(_, roles) ->
                        [
                            {
                                ActorGrant.Role = Role.Owner
                                Scope = Scope.Installation
                            }
                            for role in roles do
                                {
                                    ActorGrant.Role = role
                                    Scope = Scope.Installation
                                }
                        ]
                    | _ -> []

                AccessView.ofActor (
                    Some
                        {
                            Actor =
                                {
                                    ActorId = proposal.After.Actor.ActorId
                                    Principal = proposal.After.Actor.Principal
                                    Enabled = true
                                    Profile = Some profile
                                }
                            Grants = grants
                        }
                )
            | PeopleChange.Admitted(profile, grants) ->
                AccessView.ofActor (
                    Some
                        {
                            Actor =
                                {
                                    ActorId = proposal.After.Actor.ActorId
                                    Principal = proposal.After.Actor.Principal
                                    Enabled = true
                                    Profile = Some profile
                                }
                            Grants = grants
                        }
                )
            | PeopleChange.ProfileSet profile ->
                { view.Target with
                    Actor =
                        view.Target.Actor
                        |> Option.map (fun actor -> { actor with Profile = Some profile })
                }
            | PeopleChange.ProfileErased ->
                { view.Target with
                    Actor =
                        view.Target.Actor |> Option.map (fun actor -> { actor with Profile = None })
                }
            | PeopleChange.Enabled
            | PeopleChange.Disabled ->
                { view.Target with
                    Actor =
                        view.Target.Actor
                        |> Option.map (fun actor -> { actor with Enabled = after.Enabled })
                }
            | PeopleChange.Granted(role, _)
            | PeopleChange.Revoked(role, _) ->
                let update roles =
                    match proposal.Change with
                    | PeopleChange.Granted _ -> role :: (List.filter ((<>) role) roles)
                    | _ -> List.filter ((<>) role) roles

                match resource with
                | Scope.Installation ->
                    { view.Target with
                        Installation = update view.Target.Installation
                    }
                | Scope.Case id ->
                    { view.Target with
                        Cases =
                            Map.add
                                id
                                (Map.tryFind id view.Target.Cases
                                 |> Option.defaultValue []
                                 |> update)
                                view.Target.Cases
                    }

        let access (target: AccessView) =
            target.Actor
            |> Option.map (fun actor -> Access.explain actor.Principal target resource)
            |> Option.defaultValue []

        {
            Basis = ServiceContext.basis services view.ObservedAt
            Effects = effects
            Target =
                view.Target.Actor
                |> Option.map (fun actor ->
                    ActorReviewItem.ofActor (Map.find actor.ActorId view.TargetRoles) actor)
            AccessScope = scope
            Before =
                { before with
                    Access = access view.Target
                }
            After = { after with Access = access proposed }
        }
