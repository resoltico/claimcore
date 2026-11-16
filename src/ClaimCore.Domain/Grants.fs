namespace ClaimCore.Domain

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

module GrantTarget =
    let canonical =
        function
        | GrantTarget.CaseReference raw ->
            CaseReference.parse raw
            |> Result.map (CaseReference.identity >> GrantTarget.CaseReference)
            |> Result.defaultValue (GrantTarget.CaseReference raw)
        | target -> target

type Grant<'scope> = { Role: Role; Scope: 'scope }

type GrantInput = Grant<GrantTarget>

module PeopleAdmission =
    [<Literal>]
    let MinimumGrants = 1

    [<Literal>]
    let MaximumGrants = 48
