namespace ClaimCore.Domain


open System

type ActorSummary =
    {
        ActorId: Guid
        Principal: PrincipalKey
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
