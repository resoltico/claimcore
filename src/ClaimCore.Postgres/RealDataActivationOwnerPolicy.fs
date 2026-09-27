namespace ClaimCore.Postgres

open ClaimCore.Application
open ClaimCore.Domain

module internal RealDataActivationOwnerPolicy =
    let current context (live: ActorAuthority) revision action =
        let grant =
            ActorAuthorization.authorizeAtRevision
                context.Binding.Principal
                live
                context.Binding.GrantRevision
                action
                ResourceScope.Installation

        let owner =
            live.Grants
            |> List.exists (fun value ->
                value.Scope = GrantScope.Installation && value.Role = Role.Owner)

        match grant with
        | AuthorizationDecision.Available(actorId, _) ->
            actorId = context.Binding.ActorId && revision = live.GrantRevision && owner
        | AuthorizationDecision.Unavailable -> false
