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
type AuthorityAct<'grant> =
    | Register
    | Grant of 'grant
    | Revoke of 'grant
    | Enable
    | Disable
    | ProvisionOwner

type AuthoredAuthorityAct = AuthorityAct<AuthoredGrant>

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
        | AuthorityAct.Register -> AuthorityKind.Register
        | AuthorityAct.Grant _ -> AuthorityKind.Grant
        | AuthorityAct.Revoke _ -> AuthorityKind.Revoke
        | AuthorityAct.Enable -> AuthorityKind.Enable
        | AuthorityAct.Disable -> AuthorityKind.Disable
        | AuthorityAct.ProvisionOwner -> AuthorityKind.ProvisionOwner

    let traverse grant =
        function
        | AuthorityAct.Grant value -> grant value |> Result.map AuthorityAct.Grant
        | AuthorityAct.Revoke value -> grant value |> Result.map AuthorityAct.Revoke
        | AuthorityAct.Register -> Ok AuthorityAct.Register
        | AuthorityAct.Enable -> Ok AuthorityAct.Enable
        | AuthorityAct.Disable -> Ok AuthorityAct.Disable
        | AuthorityAct.ProvisionOwner -> Ok AuthorityAct.ProvisionOwner

    let map grant act =
        traverse (grant >> Ok) act
        |> Result.defaultWith (fun () -> invalidOp "A total authority mapping cannot fail.")
