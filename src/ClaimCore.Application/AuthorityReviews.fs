namespace ClaimCore.Application

open ClaimCore.Contract
open ClaimCore.Domain

/// Review display is derived from an already-admitted projection; it decides no authority rules.
module internal AuthorityReviews =
    let private grant role caseId (view: AuthorityReadView) =
        let scope, roles =
            match caseId with
            | None -> GrantTarget.Installation, view.Target.Installation
            | Some id ->
                let record = view.TargetCase |> Option.get

                GrantTarget.CaseReference(CaseReference.format record.Snapshot.Reference),
                (view.Target.Cases |> Map.tryFind id |> Option.defaultValue [])

        let value: AuthorityGrant = { Role = role; Scope = scope }
        value, (if List.contains role roles then [ value ] else [])

    let describe (view: AuthorityReadView) projection =
        let before: AuthorityState =
            {
                Enabled = view.Target.Actor |> Option.exists _.Enabled
                Grants = []
            }

        let before, after =
            match projection with
            | AuthorityProjection.Register _ -> before, { before with Enabled = true }
            | AuthorityProjection.ProvisionOwner _ ->
                before,
                {
                    Enabled = true
                    Grants =
                        [
                            {
                                Role = Role.Owner
                                Scope = GrantTarget.Installation
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

        {
            Target = view.Target.Actor |> Option.map Actor.describe
            Before = before
            After = after
        }
