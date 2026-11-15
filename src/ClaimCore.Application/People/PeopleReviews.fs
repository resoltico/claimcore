namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Reviews describe admitted domain changes, independently of physical effect sequencing.
module internal PeopleReviews =
    let describe services (view: PeopleExecutionFacts) (proposal: PeopleSubjectEffect) =
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

        let beforeAccess = view.State |> Option.map PeopleSubject.access
        let proposed = AccessView.ofActor (Some proposal.After)

        // Select review facts from Domain states; this does not compute a second transition.
        let relevantGrant (grant: ActorGrant) =
            match proposal.Change with
            | PeopleChange.Registered _
            | PeopleChange.Admitted _
            | PeopleChange.OwnerProvisioned _ -> true
            | PeopleChange.Granted(role, scope)
            | PeopleChange.Revoked(role, scope) -> grant.Role = role && grant.Scope = scope
            | _ -> false

        let project (value: ActorAccess option) : PeopleState =
            {
                Enabled = value |> Option.exists (fun value -> value.Actor.Enabled)
                Profile = value |> Option.bind (fun value -> value.Actor.Profile)
                Grants =
                    value
                    |> Option.map (fun value ->
                        value.Grants |> List.filter relevantGrant |> List.map grantView)
                    |> Option.defaultValue []
                Access = []
            }

        let before = project beforeAccess
        let after = project (Some proposal.After)

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
                    Access = access (AccessView.ofActor beforeAccess)
                }
            After = { after with Access = access proposed }
        }
