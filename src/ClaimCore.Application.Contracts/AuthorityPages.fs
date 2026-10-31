namespace ClaimCore.Application.Contracts

open ClaimCore.Domain


open System

type ActorReviewPage =
    {
        Items: Actor list
        NextCursor: string option
    }

type AuthorityGrantPage =
    {
        Actor: Actor
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

module AuthorityHistoryFacts =
    let ofAct target (act: AuthorityAct<ScopedGrant>) =
        match act with
        | AuthorityAct.ProvisionOwner -> AuthorityHistoryFacts.OwnerProvisioned target
        | AuthorityAct.Register -> AuthorityHistoryFacts.Registered target
        | AuthorityAct.Grant grant -> AuthorityHistoryFacts.Granted grant
        | AuthorityAct.Revoke grant -> AuthorityHistoryFacts.Revoked grant
        | AuthorityAct.Enable -> AuthorityHistoryFacts.Enabled
        | AuthorityAct.Disable -> AuthorityHistoryFacts.Disabled

    let kind =
        function
        | AuthorityHistoryFacts.OwnerProvisioned _ -> AuthorityKind.ProvisionOwner
        | AuthorityHistoryFacts.Registered _ -> AuthorityKind.Register
        | AuthorityHistoryFacts.Granted _ -> AuthorityKind.Grant
        | AuthorityHistoryFacts.Revoked _ -> AuthorityKind.Revoke
        | AuthorityHistoryFacts.Enabled -> AuthorityKind.Enable
        | AuthorityHistoryFacts.Disabled -> AuthorityKind.Disable

type AuthorityHistoryEntry =
    {
        Sequence: Sequence
        RecordedAt: DateTimeOffset
        RecordedBy: ActorId
        TargetActorId: ActorId
        Facts: AuthorityHistoryFacts
    }

type AuthorityHistoryPage =
    {
        Entries: AuthorityHistoryEntry list
        NextCursor: string option
    }
