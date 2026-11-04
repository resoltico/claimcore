namespace ClaimCore.Application.Contracts

open ClaimCore.Domain


open System

/// Present only when the viewer can read cases; zero means a known scoped count.
type ActorResponsibleCases =
    {
        Count: int64
        ResponsibleActor: ActorId
    }

    member _.View = CaseView.All

/// Directory details are explicit; contact never becomes part of a reusable actor reference.
type ActorReviewItem =
    {
        Actor: ActorRef
        Principal: PrincipalKey
        Contact: ContactText option
        ResponsibleCases: ActorResponsibleCases option
    }

module ActorReviewItem =
    let ofActor (actor: Actor) =
        {
            Actor = Actor.currentReference actor
            Principal = actor.Principal
            Contact = actor.Profile |> Option.bind _.Contact
            ResponsibleCases = None
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
