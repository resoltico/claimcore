namespace ClaimCore.RecordFormat

open System
open ClaimCore.Domain

[<RequireQualifiedAccess>]
type RecordedGrantScope =
    | Installation
    | Case of CaseId * CaseReference

type RecordedGrant = Grant<RecordedGrantScope>

/// Each payload owns only the information needed by its durable effect.
[<RequireQualifiedAccess>]
type AuthorityEffect<'grant> =
    | Register of PrincipalInput
    | ProvisionOwner of PrincipalInput
    | SetProfile of ActorProfile
    | EraseProfile
    | Grant of 'grant
    | Revoke of 'grant
    | Enable
    | Disable

module AuthorityEffect =
    let kind =
        function
        | AuthorityEffect.Register _ -> AuthorityKind.Register
        | AuthorityEffect.ProvisionOwner _ -> AuthorityKind.ProvisionOwner
        | AuthorityEffect.SetProfile _ -> AuthorityKind.SetProfile
        | AuthorityEffect.EraseProfile -> AuthorityKind.EraseProfile
        | AuthorityEffect.Grant _ -> AuthorityKind.Grant
        | AuthorityEffect.Revoke _ -> AuthorityKind.Revoke
        | AuthorityEffect.Enable -> AuthorityKind.Enable
        | AuthorityEffect.Disable -> AuthorityKind.Disable

    let traverse grant =
        function
        | AuthorityEffect.Grant value -> grant value |> Result.map AuthorityEffect.Grant
        | AuthorityEffect.Revoke value -> grant value |> Result.map AuthorityEffect.Revoke
        | AuthorityEffect.Register value -> Ok(AuthorityEffect.Register value)
        | AuthorityEffect.ProvisionOwner value -> Ok(AuthorityEffect.ProvisionOwner value)
        | AuthorityEffect.SetProfile value -> Ok(AuthorityEffect.SetProfile value)
        | AuthorityEffect.EraseProfile -> Ok AuthorityEffect.EraseProfile
        | AuthorityEffect.Enable -> Ok AuthorityEffect.Enable
        | AuthorityEffect.Disable -> Ok AuthorityEffect.Disable

    let map grant effect =
        traverse (grant >> Ok) effect
        |> Result.defaultWith (fun () -> invalidOp "A total effect mapping cannot fail.")

type AuthorityEffectRecord =
    {
        OperationId: Guid
        TargetActorId: ActorId
        Effect: AuthorityEffect<RecordedGrant>
    }
