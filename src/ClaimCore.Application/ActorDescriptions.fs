namespace ClaimCore.Application

open ClaimCore.Domain

module internal Actor =
    let describe (actor: Actor) : ActorSummary =
        {
            ActorId = actor.ActorId
            Principal = actor.Principal
            Enabled = actor.Enabled
        }
