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
    | Admit

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

type AuthorityAdmission<'grant, 'profile> =
    {
        Profile: 'profile
        Grants: 'grant list
    }

module AuthorityAdmission =
    [<Literal>]
    let MaximumGrants = 48

[<RequireQualifiedAccess>]
type AuthorityAct<'grant, 'profile> =
    | Register of 'profile
    | Admit of AuthorityAdmission<'grant, 'profile>
    | Grant of 'grant
    | Revoke of 'grant
    | Enable
    | Disable
    | ProvisionOwner of profile: 'profile * roles: Role list
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
        | AuthorityAct.Admit _ -> AuthorityKind.Admit
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
        | AuthorityAct.Admit value ->
            profile value.Profile
            |> Result.bind (fun profile ->
                value.Grants
                |> List.fold
                    (fun state value ->
                        state
                        |> Result.bind (fun values ->
                            grant value |> Result.map (fun value -> value :: values)))
                    (Ok [])
                |> Result.map (fun grants ->
                    AuthorityAct.Admit
                        {
                            Profile = profile
                            Grants = List.rev grants
                        }))
        | AuthorityAct.SetProfile value -> profile value |> Result.map AuthorityAct.SetProfile
        | AuthorityAct.ProvisionOwner(value, roles) ->
            profile value
            |> Result.map (fun value -> AuthorityAct.ProvisionOwner(value, roles))
        | AuthorityAct.Enable -> Ok AuthorityAct.Enable
        | AuthorityAct.Disable -> Ok AuthorityAct.Disable
        | AuthorityAct.EraseProfile -> Ok AuthorityAct.EraseProfile

    let map grant profile act =
        traverse (grant >> Ok) (profile >> Ok) act
        |> Result.defaultWith (fun () -> invalidOp "A total authority mapping cannot fail.")
