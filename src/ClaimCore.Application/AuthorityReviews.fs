namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Reviews describe admitted domain changes, independently of physical effect sequencing.
module internal AuthorityReviews =
    let describe (view: AuthorityExecutionFacts) (proposal: AuthorityProposal) =
        let profile = view.Target.Actor |> Option.bind _.Profile

        let before: AuthorityState =
            {
                Enabled = view.Target.Actor |> Option.exists _.Enabled
                Profile = profile
                Grants = []
                Access = []
            }

        let resource, scope =
            match proposal.Change with
            | AuthorityChange.Granted(_, Scope.Case id)
            | AuthorityChange.Revoked(_, Scope.Case id) ->
                let record =
                    view.TargetCase
                    |> Option.defaultWith (fun () ->
                        invalidOp "A scoped authority proposal requires its case.")

                Scope.Case id, GrantScope.Case(id, record.Snapshot.Reference)
            | _ -> Scope.Installation, GrantScope.Installation

        let before, after =
            match proposal.Change with
            | AuthorityChange.Registered profile ->
                before,
                { before with
                    Enabled = true
                    Profile = Some profile
                }
            | AuthorityChange.OwnerProvisioned profile ->
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
                        ]
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
            | AuthorityChange.OwnerProvisioned profile ->
                let grants =
                    match proposal.Change with
                    | AuthorityChange.OwnerProvisioned _ ->
                        [
                            {
                                ActorGrant.Role = Role.Owner
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
            Target = view.Target.Actor |> Option.map ActorReviewItem.ofActor
            AccessScope = scope
            Before =
                { before with
                    Access = access view.Target
                }
            After = { after with Access = access proposed }
        }
