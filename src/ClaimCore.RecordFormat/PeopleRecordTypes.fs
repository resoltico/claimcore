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
type PeopleEffect<'grant> =
    | Register of PrincipalInput
    | ProvisionOwner of PrincipalInput
    | SetProfile of ActorProfile
    | EraseProfile
    | Grant of 'grant
    | Revoke of 'grant
    | Enable
    | Disable

module PeopleEffect =
    let kind =
        function
        | PeopleEffect.Register _ -> PeopleActKind.Register
        | PeopleEffect.ProvisionOwner _ -> PeopleActKind.ProvisionOwner
        | PeopleEffect.SetProfile _ -> PeopleActKind.SetProfile
        | PeopleEffect.EraseProfile -> PeopleActKind.EraseProfile
        | PeopleEffect.Grant _ -> PeopleActKind.Grant
        | PeopleEffect.Revoke _ -> PeopleActKind.Revoke
        | PeopleEffect.Enable -> PeopleActKind.Enable
        | PeopleEffect.Disable -> PeopleActKind.Disable

    let traverse grant =
        function
        | PeopleEffect.Grant value -> grant value |> Result.map PeopleEffect.Grant
        | PeopleEffect.Revoke value -> grant value |> Result.map PeopleEffect.Revoke
        | PeopleEffect.Register value -> Ok(PeopleEffect.Register value)
        | PeopleEffect.ProvisionOwner value -> Ok(PeopleEffect.ProvisionOwner value)
        | PeopleEffect.SetProfile value -> Ok(PeopleEffect.SetProfile value)
        | PeopleEffect.EraseProfile -> Ok PeopleEffect.EraseProfile
        | PeopleEffect.Enable -> Ok PeopleEffect.Enable
        | PeopleEffect.Disable -> Ok PeopleEffect.Disable

    let map grant effect =
        traverse (grant >> Ok) effect
        |> Result.defaultWith (fun () -> invalidOp "A total effect mapping cannot fail.")

type PeopleEffectRecord =
    {
        OperationId: Guid
        TargetActorId: ActorId
        Effect: PeopleEffect<RecordedGrant>
    }
