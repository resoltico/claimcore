namespace ClaimCore.Domain

open System

[<RequireQualifiedAccess>]
type AuthorityKind =
    | ProvisionOwner
    | Register
    | Grant
    | Revoke
    | Enable
    | Disable
    | SetProfile
    | EraseProfile

/// Unvalidated subject facts; Application admits the canonical principal key.
type PrincipalInput =
    {
        Kind: PrincipalKind
        Issuer: string
        Subject: string
    }

[<RequireQualifiedAccess>]
type GrantTarget =
    | Installation
    | CaseReference of string
    | CaseId of CaseId

type Grant<'scope> = { Role: Role; Scope: 'scope }

type AuthoredGrant = Grant<GrantTarget>

[<RequireQualifiedAccess>]
type AuthorityAct<'grant, 'profile> =
    | Register of 'profile
    | Grant of 'grant
    | Revoke of 'grant
    | Enable
    | Disable
    | ProvisionOwner of 'profile
    | SetProfile of 'profile
    | EraseProfile

type AuthoredAuthorityAct = AuthorityAct<AuthoredGrant, ActorProfileInput>

type AuthorityRequest =
    {
        OperationId: Guid
        Target: PrincipalInput
        Act: AuthoredAuthorityAct
    }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module AuthorityKind =
    let vocabulary =
        Vocabulary.ofUnion<AuthorityKind> "act" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

module AuthorityAct =
    let kind =
        function
        | AuthorityAct.Register _ -> AuthorityKind.Register
        | AuthorityAct.Grant _ -> AuthorityKind.Grant
        | AuthorityAct.Revoke _ -> AuthorityKind.Revoke
        | AuthorityAct.Enable -> AuthorityKind.Enable
        | AuthorityAct.Disable -> AuthorityKind.Disable
        | AuthorityAct.ProvisionOwner _ -> AuthorityKind.ProvisionOwner
        | AuthorityAct.SetProfile _ -> AuthorityKind.SetProfile
        | AuthorityAct.EraseProfile -> AuthorityKind.EraseProfile

    let traverse grant profile =
        function
        | AuthorityAct.Grant value -> grant value |> Result.map AuthorityAct.Grant
        | AuthorityAct.Revoke value -> grant value |> Result.map AuthorityAct.Revoke
        | AuthorityAct.Register value -> profile value |> Result.map AuthorityAct.Register
        | AuthorityAct.SetProfile value -> profile value |> Result.map AuthorityAct.SetProfile
        | AuthorityAct.ProvisionOwner value ->
            profile value |> Result.map AuthorityAct.ProvisionOwner
        | AuthorityAct.Enable -> Ok AuthorityAct.Enable
        | AuthorityAct.Disable -> Ok AuthorityAct.Disable
        | AuthorityAct.EraseProfile -> Ok AuthorityAct.EraseProfile

    let map grant profile act =
        traverse (grant >> Ok) (profile >> Ok) act
        |> Result.defaultWith (fun () -> invalidOp "A total authority mapping cannot fail.")
