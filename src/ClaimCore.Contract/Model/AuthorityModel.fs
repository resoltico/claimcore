namespace ClaimCore.Contract

open System

/// Unvalidated subject facts; Application admits the canonical principal key.
type Principal =
    {
        Kind: string
        Issuer: string
        Subject: string
    }

/// Caller-authored scope; accepted grants resolve case references to case IDs.
[<RequireQualifiedAccess>]
type GrantTarget =
    | Installation
    | CaseReference of string

type GrantChange = { Role: string; Scope: GrantTarget }

[<RequireQualifiedAccess>]
type AuthorityAct =
    | Register
    | Grant of GrantChange
    | Revoke of GrantChange
    | Enable
    | Disable

type AuthorityRequest =
    {
        OperationId: Guid
        Target: Principal
        Act: AuthorityAct
    }
