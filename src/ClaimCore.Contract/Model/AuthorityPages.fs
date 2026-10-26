namespace ClaimCore.Contract

open ClaimCore.Domain

open System

type AuthorityGrant = { Role: Role; Scope: GrantTarget }

type ActorSummary =
    {
        ActorId: Guid
        Principal: Principal
        Enabled: bool
    }

type ActorReviewPage =
    {
        Items: ActorSummary list
        NextCursor: string option
    }

type AuthorityGrantPage =
    {
        Actor: ActorSummary
        Items: AuthorityGrant list
        NextCursor: string option
    }
