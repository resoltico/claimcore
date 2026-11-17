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
        Controls: TaskOffer list
    }

type PeopleGrantPage =
    {
        Basis: WorkDateBasis
        Actor: ActorReviewItem
        Revision: Revision
        Offers: TaskId list
        Controls: TaskOffer list
        ProfileSeed: ActorProfileInput option
        CanTerminateSessions: bool
        Items: GrantReadModel list
        NextCursor: string option
    }

type PeopleHistoryEntry =
    {
        OperationId: OperationId
        Position: EffectPosition
        OperationHash: EntryHash
        RecordedAt: DateTimeOffset
        RecordedBy: ActorRef
        Target: ActorRef
        Task: TaskId
        Changes: ClaimCore.Domain.Changes.PersonPartDescription
        PrivateAvailable: bool
        References: ChangeReferences
    }

type PeopleHistoryPage =
    {
        Basis: WorkDateBasis
        Entries: PeopleHistoryEntry list
        NextCursor: string option
    }
