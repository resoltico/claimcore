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
        Items: AuthoredGrant list
        NextCursor: string option
    }

[<RequireQualifiedAccess>]
type AuthorityHistoryFacts =
    | OwnerProvisioned of PrincipalKey
    | Registered of PrincipalKey
    | Granted of AuthoredGrant
    | Revoked of AuthoredGrant
    | Enabled
    | Disabled

module AuthorityHistoryFacts =
    let ofCommand (command: AuthorityCommand) =
        match command.Act with
        | AuthorityAct.ProvisionOwner -> AuthorityHistoryFacts.OwnerProvisioned command.Target
        | AuthorityAct.Register -> AuthorityHistoryFacts.Registered command.Target
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
