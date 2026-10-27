namespace ClaimCore.Application

open ClaimCore.Domain

module internal Actor =
    let describe (actor: Actor) : ActorSummary =
        {
            ActorId = actor.ActorId
            Principal = PrincipalKey.describe actor.Principal
            Enabled = actor.Enabled
        }
