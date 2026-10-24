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

type GrantChange =
    {
        Role: string
        Scope: GrantTarget
        Active: bool
    }

[<RequireQualifiedAccess>]
type AuthorityAct =
    | RegisterActor
    | SetGrant of GrantChange
    | SetEnabled of bool

type AuthorityRequest =
    {
        OperationId: Guid
        Target: Principal
        Act: AuthorityAct
    }
