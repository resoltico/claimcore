namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Reviews describe admitted domain changes, independently of physical effect sequencing.
module internal AuthorityReviews =
    let describe services (view: AuthorityExecutionFacts) (proposal: AuthorityProposal) =
        let profile = view.Target.Actor |> Option.bind _.Profile

        let before: AuthorityState =
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
                    invalidOp "A scoped authority proposal requires its case.")

        let grantView (value: Grant<Scope>) : ScopedGrant =
            {
                Role = value.Role
                Scope = scopeView value.Scope
            }

        let effects =
            match proposal.Change with
            | AuthorityChange.Registered profile ->
                [
                    AuthorityReviewEffect.Register proposal.Principal
                    AuthorityReviewEffect.SetProfile profile
                ]
            | AuthorityChange.OwnerProvisioned(profile, roles) ->
                [
                    AuthorityReviewEffect.ProvisionOwner proposal.Principal
                    AuthorityReviewEffect.SetProfile profile
                    for role in roles do
                        AuthorityReviewEffect.Grant
                            {
                                Role = role
                                Scope = GrantScope.Installation
                            }
                ]
            | AuthorityChange.Admitted(profile, grants) ->
                AuthorityReviewEffect.Register proposal.Principal
                :: AuthorityReviewEffect.SetProfile profile
                :: (grants |> List.map (grantView >> AuthorityReviewEffect.Grant))
            | AuthorityChange.ProfileSet profile -> [ AuthorityReviewEffect.SetProfile profile ]
            | AuthorityChange.ProfileErased -> [ AuthorityReviewEffect.EraseProfile ]
            | AuthorityChange.Enabled -> [ AuthorityReviewEffect.Enable ]
            | AuthorityChange.Disabled -> [ AuthorityReviewEffect.Disable ]
            | AuthorityChange.Granted(role, scope) ->
                [ AuthorityReviewEffect.Grant(grantView { Role = role; Scope = scope }) ]
            | AuthorityChange.Revoked(role, scope) ->
                [ AuthorityReviewEffect.Revoke(grantView { Role = role; Scope = scope }) ]

        let resource, scope =
            match proposal.Change with
            | AuthorityChange.Granted(_, Scope.Case id)
            | AuthorityChange.Revoked(_, Scope.Case id) -> Scope.Case id, scopeView (Scope.Case id)
            | _ -> Scope.Installation, GrantScope.Installation

        let before, after =
            match proposal.Change with
            | AuthorityChange.Registered profile ->
                before,
                { before with
                    Enabled = true
                    Profile = Some profile
                }
            | AuthorityChange.OwnerProvisioned(profile, roles) ->
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
            | AuthorityChange.Admitted(profile, grants) ->
                before,
                { before with
                    Enabled = true
                    Profile = Some profile
                    Grants = List.map grantView grants
                }
            | AuthorityChange.ProfileSet profile -> before, { before with Profile = Some profile }
            | AuthorityChange.ProfileErased -> before, { before with Profile = None }
            | AuthorityChange.Enabled -> before, { before with Enabled = true }
            | AuthorityChange.Disabled -> before, { before with Enabled = false }
            | AuthorityChange.Granted(role, _)
            | AuthorityChange.Revoked(role, _) ->
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
                    | AuthorityChange.Granted _ -> [ grant ]
                    | _ -> []

                before, { before with Grants = grants }

        let proposed =
            match proposal.Change with
            | AuthorityChange.Registered profile
            | AuthorityChange.OwnerProvisioned(profile, _) ->
                let grants =
                    match proposal.Change with
                    | AuthorityChange.OwnerProvisioned(_, roles) ->
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

                AuthorityView.ofActor (
                    Some
                        {
                            Actor =
                                {
                                    ActorId = proposal.TargetActorId
                                    Principal = proposal.Principal
                                    Enabled = true
                                    Profile = Some profile
                                }
                            Grants = grants
                        }
                )
            | AuthorityChange.Admitted(profile, grants) ->
                AuthorityView.ofActor (
                    Some
                        {
                            Actor =
                                {
                                    ActorId = proposal.TargetActorId
                                    Principal = proposal.Principal
                                    Enabled = true
                                    Profile = Some profile
                                }
                            Grants = grants
                        }
                )
            | AuthorityChange.ProfileSet profile ->
                { view.Target with
                    Actor =
                        view.Target.Actor
                        |> Option.map (fun actor -> { actor with Profile = Some profile })
                }
            | AuthorityChange.ProfileErased ->
                { view.Target with
                    Actor =
                        view.Target.Actor |> Option.map (fun actor -> { actor with Profile = None })
                }
            | AuthorityChange.Enabled
            | AuthorityChange.Disabled ->
                { view.Target with
                    Actor =
                        view.Target.Actor
                        |> Option.map (fun actor -> { actor with Enabled = after.Enabled })
                }
            | AuthorityChange.Granted(role, _)
            | AuthorityChange.Revoked(role, _) ->
                let update roles =
                    match proposal.Change with
                    | AuthorityChange.Granted _ -> role :: (List.filter ((<>) role) roles)
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

        let access (target: AuthorityView) =
            target.Actor
            |> Option.map (fun actor -> Authority.explain actor.Principal target resource)
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
