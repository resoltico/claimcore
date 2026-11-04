namespace ClaimCore.Application.Contracts

open ClaimCore.Domain


open System

/// Directory details are explicit; contact never becomes part of a reusable actor reference.
type ActorReviewItem =
    {
        Actor: ActorRef
        Principal: PrincipalKey
        Contact: ContactText option
    }

module ActorReviewItem =
    let ofActor (actor: Actor) =
        {
            Actor = Actor.currentReference actor
            Principal = actor.Principal
            Contact = actor.Profile |> Option.bind _.Contact
        }

type ActorReviewPage =
    {
        Items: ActorReviewItem list
        NextCursor: string option
    }

type AuthorityGrantPage =
    {
        Actor: ActorReviewItem
        Items: ScopedGrant list
        NextCursor: string option
    }

[<RequireQualifiedAccess>]
type AuthorityHistoryFacts =
    | OwnerProvisioned of PrincipalKey
    | Registered of PrincipalKey
    | Granted of ScopedGrant
    | Revoked of ScopedGrant
    | Enabled
    | Disabled
    | ProfileSet of ActorProfile option
    | ProfileErased

module AuthorityHistoryFacts =
    let kind =
        function
        | AuthorityHistoryFacts.OwnerProvisioned _ -> AuthorityKind.ProvisionOwner
        | AuthorityHistoryFacts.Registered _ -> AuthorityKind.Register
        | AuthorityHistoryFacts.Granted _ -> AuthorityKind.Grant
        | AuthorityHistoryFacts.Revoked _ -> AuthorityKind.Revoke
        | AuthorityHistoryFacts.Enabled -> AuthorityKind.Enable
        | AuthorityHistoryFacts.Disabled -> AuthorityKind.Disable
        | AuthorityHistoryFacts.ProfileSet _ -> AuthorityKind.SetProfile
        | AuthorityHistoryFacts.ProfileErased -> AuthorityKind.EraseProfile

type AuthorityHistoryEntry =
    {
        Sequence: Sequence
        RecordedAt: DateTimeOffset
        RecordedBy: ActorRef
        Target: ActorRef
        Facts: AuthorityHistoryFacts
    }

type AuthorityHistoryPage =
    {
        Entries: AuthorityHistoryEntry list
        NextCursor: string option
    }
