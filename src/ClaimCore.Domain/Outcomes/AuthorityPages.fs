namespace ClaimCore.Domain


open System

type ActorReviewPage =
    {
        Items: Actor list
        NextCursor: string option
    }

type AuthorityGrantPage =
    {
        Actor: Actor
        Items: AuthoredGrant list
        NextCursor: string option
    }
