namespace ClaimCore.Domain

open System

[<RequireQualifiedAccess>]
type PeopleActKind =
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

type PeopleAdmission<'grant, 'profile> =
    {
        Profile: 'profile
        Grants: 'grant list
    }

module PeopleAdmission =
    [<Literal>]
    let MinimumGrants = 1

    [<Literal>]
    let MaximumGrants = 48

[<RequireQualifiedAccess>]
type PeopleAct<'grant, 'profile> =
    | Register of 'profile
    | Admit of PeopleAdmission<'grant, 'profile>
    | Grant of 'grant
    | Revoke of 'grant
    | Enable
    | Disable
    | ProvisionOwner of profile: 'profile * roles: Role list
    | SetProfile of 'profile
    | EraseProfile

type AuthoredPeopleAct = PeopleAct<AuthoredGrant, ActorProfileInput>

type PeopleRequest =
    {
        OperationId: Guid
        Target: PrincipalInput
        Act: AuthoredPeopleAct
    }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module PeopleActKind =
    let vocabulary =
        Vocabulary.ofUnion<PeopleActKind> "task" TokenCasing.CamelCase (Some "people.")

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

    /// Existing-account controls exclude account creation and installation bootstrap.
    let targetsExistingActor =
        function
        | PeopleActKind.ProvisionOwner
        | PeopleActKind.Register
        | PeopleActKind.Admit -> false
        | PeopleActKind.Grant
        | PeopleActKind.Revoke
        | PeopleActKind.Enable
        | PeopleActKind.Disable
        | PeopleActKind.SetProfile
        | PeopleActKind.EraseProfile -> true

module PeopleAct =
    let kind =
        function
        | PeopleAct.Register _ -> PeopleActKind.Register
        | PeopleAct.Admit _ -> PeopleActKind.Admit
        | PeopleAct.Grant _ -> PeopleActKind.Grant
        | PeopleAct.Revoke _ -> PeopleActKind.Revoke
        | PeopleAct.Enable -> PeopleActKind.Enable
        | PeopleAct.Disable -> PeopleActKind.Disable
        | PeopleAct.ProvisionOwner _ -> PeopleActKind.ProvisionOwner
        | PeopleAct.SetProfile _ -> PeopleActKind.SetProfile
        | PeopleAct.EraseProfile -> PeopleActKind.EraseProfile

    let traverse grant profile =
        function
        | PeopleAct.Grant value -> grant value |> Result.map PeopleAct.Grant
        | PeopleAct.Revoke value -> grant value |> Result.map PeopleAct.Revoke
        | PeopleAct.Register value -> profile value |> Result.map PeopleAct.Register
        | PeopleAct.Admit value ->
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
                    PeopleAct.Admit
                        {
                            Profile = profile
                            Grants = List.rev grants
                        }))
        | PeopleAct.SetProfile value -> profile value |> Result.map PeopleAct.SetProfile
        | PeopleAct.ProvisionOwner(value, roles) ->
            profile value
            |> Result.map (fun value -> PeopleAct.ProvisionOwner(value, roles))
        | PeopleAct.Enable -> Ok PeopleAct.Enable
        | PeopleAct.Disable -> Ok PeopleAct.Disable
        | PeopleAct.EraseProfile -> Ok PeopleAct.EraseProfile

    let map grant profile act =
        traverse (grant >> Ok) (profile >> Ok) act
        |> Result.defaultWith (fun () -> invalidOp "A total people mapping cannot fail.")
