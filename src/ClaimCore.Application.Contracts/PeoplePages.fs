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
    member _.Lifecycle = CaseListFilter.activeOpen<unit>.Lifecycles |> List.exactlyOne
    member _.Status = CaseListFilter.activeOpen<unit>.Statuses |> List.exactlyOne

/// Directory details are explicit; contact never becomes part of a reusable actor reference.
type ActorReviewItem =
    {
        Actor: ActorRef
        Principal: PrincipalKey
        Contact: ContactText option
        State: AccountState
        Roles: ActorRoleSummary list
        ResponsibleCases: ActorResponsibleCases option
    }

module ActorReviewItem =
    let ofActor roles (actor: Actor) =
        {
            State = AccountState.classify actor.Enabled (not (List.isEmpty roles))
            Roles = ActorRoleSummary.ordered roles
            Actor = Actor.currentReference actor
            Principal = actor.Principal
            Contact = actor.Profile |> Option.bind _.Contact
            ResponsibleCases = None
        }

type ActorReviewPage =
    {
        Basis: WorkDateBasis
        Items: ActorReviewItem list
        NextCursor: string option
    }

type GrantReadModel =
    {
        Role: Role
        Scope: GrantScope
        GrantedAt: DateTimeOffset
        GrantedBy: ActorRef
    }

type PeopleGrantPage =
    {
        Basis: WorkDateBasis
        Actor: ActorReviewItem
        Offers: PeopleActKind list
        ProfileSeed: ActorProfileInput option
        Items: GrantReadModel list
        NextCursor: string option
    }

[<RequireQualifiedAccess>]
type PeopleHistoryFacts =
    | OwnerProvisioned of PrincipalKey
    | Registered of PrincipalKey
    | Granted of ScopedGrant
    | Revoked of ScopedGrant
    | Enabled
    | Disabled
    | ProfileSet of ActorProfile option
    | ProfileErased

module PeopleHistoryFacts =
    let kind =
        function
        | PeopleHistoryFacts.OwnerProvisioned _ -> PeopleActKind.ProvisionOwner
        | PeopleHistoryFacts.Registered _ -> PeopleActKind.Register
        | PeopleHistoryFacts.Granted _ -> PeopleActKind.Grant
        | PeopleHistoryFacts.Revoked _ -> PeopleActKind.Revoke
        | PeopleHistoryFacts.Enabled -> PeopleActKind.Enable
        | PeopleHistoryFacts.Disabled -> PeopleActKind.Disable
        | PeopleHistoryFacts.ProfileSet _ -> PeopleActKind.SetProfile
        | PeopleHistoryFacts.ProfileErased -> PeopleActKind.EraseProfile

type PeopleHistoryEntry =
    {
        OperationId: OperationId
        Position: EffectPosition
        OperationHash: EntryHash
        RecordedAt: DateTimeOffset
        RecordedBy: ActorRef
        Target: ActorRef
        Facts: PeopleHistoryFacts
    }

type PeopleHistoryPage =
    {
        Basis: WorkDateBasis
        Entries: PeopleHistoryEntry list
        NextCursor: string option
    }
