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

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type AuthorityActSlot<'grant> =
    | Bare of AuthorityAct<'grant>
    | Grant of
        inject: ('grant -> AuthorityAct<'grant>) *
        project: (AuthorityAct<'grant> -> 'grant option)

module AuthorityAct =
    let kind =
        function
        | AuthorityAct.Register -> AuthorityKind.Register
        | AuthorityAct.Grant _ -> AuthorityKind.Grant
        | AuthorityAct.Revoke _ -> AuthorityKind.Revoke
        | AuthorityAct.Enable -> AuthorityKind.Enable
        | AuthorityAct.Disable -> AuthorityKind.Disable
        | AuthorityAct.ProvisionOwner -> AuthorityKind.ProvisionOwner

    let slot<'grant> kind : AuthorityActSlot<'grant> =
        match kind with
        | AuthorityKind.ProvisionOwner -> AuthorityActSlot.Bare AuthorityAct.ProvisionOwner
        | AuthorityKind.Register -> AuthorityActSlot.Bare AuthorityAct.Register
        | AuthorityKind.Grant ->
            AuthorityActSlot.Grant(
                AuthorityAct.Grant,
                (function
                | AuthorityAct.Grant value -> Some value
                | _ -> None)
            )
        | AuthorityKind.Revoke ->
            AuthorityActSlot.Grant(
                AuthorityAct.Revoke,
                (function
                | AuthorityAct.Revoke value -> Some value
                | _ -> None)
            )
        | AuthorityKind.Enable -> AuthorityActSlot.Bare AuthorityAct.Enable
        | AuthorityKind.Disable -> AuthorityActSlot.Bare AuthorityAct.Disable

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

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module AuthorityKind =
    let vocabulary =
        Vocabulary.ofUnion<AuthorityKind> "act" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary
