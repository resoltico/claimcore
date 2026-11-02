namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Review display is derived from an already-admitted projection; it decides no authority rules.
module internal AuthorityReviews =
    let private grant role caseId (view: AuthorityExecutionFacts) =
        let scope, roles =
            match caseId with
            | None -> GrantScope.Installation, view.Target.Installation
            | Some id ->
                let record = view.TargetCase |> Option.get

                GrantScope.Case(record.CaseId, record.Snapshot.Reference),
                (view.Target.Cases |> Map.tryFind id |> Option.defaultValue [])

        let value: ScopedGrant = { Role = role; Scope = scope }
        value, (if List.contains role roles then [ value ] else [])

    let describe (view: AuthorityExecutionFacts) projection =
        let before: AuthorityState =
            {
                Enabled = view.Target.Actor |> Option.exists _.Enabled
                Grants = []
                Access = []
            }

        let before, after =
            match projection with
            | AuthorityProjection.Register _ -> before, { before with Enabled = true }
            | AuthorityProjection.ProvisionOwner _ ->
                before,
                {
                    Enabled = true
                    Access = []
                    Grants =
                        [
                            {
                                Role = Role.Owner
                                Scope = GrantScope.Installation
                            }
                        ]
                }
            | AuthorityProjection.SetEnabled(_, enabled) ->
                before, { before with Enabled = enabled }
            | AuthorityProjection.AddGrant(_, role, caseId) ->
                let value, current = grant role caseId view
                { before with Grants = current }, { before with Grants = [ value ] }
            | AuthorityProjection.RemoveGrant(_, role, caseId) ->
                let _, current = grant role caseId view
                { before with Grants = current }, before

        let resource, scope =
            match projection with
            | AuthorityProjection.AddGrant(_, _, Some id)
            | AuthorityProjection.RemoveGrant(_, _, Some id) ->
                let record = view.TargetCase |> Option.get
                Scope.Case id, GrantScope.Case(id, record.Snapshot.Reference)
            | _ -> Scope.Installation, GrantScope.Installation

        let proposed =
            match projection with
            | AuthorityProjection.Register(id, principal)
            | AuthorityProjection.ProvisionOwner(id, principal) ->
                AuthorityView.ofActor (
                    Some
                        {
                            Actor =
                                {
                                    ActorId = id
                                    Principal = principal
                                    Enabled = true
                                }
                            Grants =
                                match projection with
                                | AuthorityProjection.ProvisionOwner _ ->
                                    [
                                        {
                                            Role = Role.Owner
                                            Scope = Scope.Installation
                                        }
                                    ]
                                | _ -> []
                        }
                )
            | AuthorityProjection.SetEnabled(_, enabled) ->
                { view.Target with
                    Actor =
                        view.Target.Actor
                        |> Option.map (fun actor -> { actor with Enabled = enabled })
                }
            | AuthorityProjection.AddGrant(_, role, caseId)
            | AuthorityProjection.RemoveGrant(_, role, caseId) ->
                let update roles =
                    match projection with
                    | AuthorityProjection.AddGrant _ ->
                        if List.contains role roles then roles else roles @ [ role ]
                    | _ -> roles |> List.filter ((<>) role)

                match caseId with
                | None ->
                    { view.Target with
                        Installation = update view.Target.Installation
                    }
                | Some id ->
                    let roles =
                        view.Target.Cases |> Map.tryFind id |> Option.defaultValue [] |> update

                    { view.Target with
                        Cases = Map.add id roles view.Target.Cases
                    }

        let access (target: AuthorityView) =
            target.Actor
            |> Option.map (fun actor -> Authority.explain actor.Principal target resource)
            |> Option.defaultValue []

        {
            Target = view.Target.Actor
            AccessScope = scope
            Before =
                { before with
                    Access = access view.Target
                }
            After = { after with Access = access proposed }
        }
