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
type Principal =
    {
        Kind: string
        Issuer: string
        Subject: string
    }

[<RequireQualifiedAccess>]
type GrantTarget =
    | Installation
    | CaseReference of string

type GrantChange = { Role: string; Scope: GrantTarget }

[<RequireQualifiedAccess>]
type AuthorityAct<'grant> =
    | Register
    | Grant of 'grant
    | Revoke of 'grant
    | Enable
    | Disable
    | ProvisionOwner

type AuthoredAuthorityAct = AuthorityAct<GrantChange>

type AuthorityRequest =
    {
        OperationId: Guid
        Target: Principal
        Act: AuthoredAuthorityAct
    }

[<RequireQualifiedAccess>]
type AuthorityShape =
    | TargetOnly
    | GrantValues

module AuthorityAct =
    let kind =
        function
        | AuthorityAct.Register -> AuthorityKind.Register
        | AuthorityAct.Grant _ -> AuthorityKind.Grant
        | AuthorityAct.Revoke _ -> AuthorityKind.Revoke
        | AuthorityAct.Enable -> AuthorityKind.Enable
        | AuthorityAct.Disable -> AuthorityKind.Disable
        | AuthorityAct.ProvisionOwner -> AuthorityKind.ProvisionOwner

    let shape =
        function
        | AuthorityKind.Grant
        | AuthorityKind.Revoke -> AuthorityShape.GrantValues
        | AuthorityKind.Register
        | AuthorityKind.Enable
        | AuthorityKind.Disable
        | AuthorityKind.ProvisionOwner -> AuthorityShape.TargetOnly

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

    let payload =
        function
        | AuthorityAct.Grant grant
        | AuthorityAct.Revoke grant -> Some grant
        | AuthorityAct.Register
        | AuthorityAct.Enable
        | AuthorityAct.Disable
        | AuthorityAct.ProvisionOwner -> None

    let ofPayload kind grant =
        match kind, grant with
        | AuthorityKind.Grant, Some grant -> AuthorityAct.Grant grant
        | AuthorityKind.Revoke, Some grant -> AuthorityAct.Revoke grant
        | AuthorityKind.Register, None -> AuthorityAct.Register
        | AuthorityKind.Enable, None -> AuthorityAct.Enable
        | AuthorityKind.Disable, None -> AuthorityAct.Disable
        | AuthorityKind.ProvisionOwner, None -> AuthorityAct.ProvisionOwner
        | _ -> invalidArg (nameof grant) "The grant must match the authority act shape."

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module AuthorityKind =
    let vocabulary =
        Vocabulary.ofUnion<AuthorityKind> "act" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary
