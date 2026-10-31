namespace ClaimCore.Application.Contracts

open System
open ClaimCore.Domain

/// Authored references and retained IDs are explicitly distinguished, including UUID-shaped references.
[<RequireQualifiedAccess>]
type CaseSelector =
    | Reference of string
    | Id of CaseId

/// Read/review scopes publish the resolved retained identity, independently of authored selectors.
[<RequireQualifiedAccess>]
type GrantScope =
    | Installation
    | Case of CaseId * CaseReference

type ScopedGrant = Grant<GrantScope>

type CaseOperationRequest =
    {
        OperationId: Guid
        Target: CaseSelector
        ExpectedRevision: int64
        Act: AuthoredCaseAct
    }

type PageRequest =
    {
        AfterCursor: string option
        Limit: int
    }

type HistoryRequest =
    {
        Target: CaseSelector
        AfterCursor: string option
        Limit: int
        Detail: HistoryDetail
    }

type GrantsRequest =
    {
        ActorId: Guid
        AfterCursor: string option
        Limit: int
    }

type AuthorityHistoryRequest =
    {
        TargetActorId: Guid option
        AfterCursor: string option
        Limit: int
    }

type CaseGetRequest = { Target: CaseSelector }

type OperationLookup = { OperationId: Guid }


type CaseListRequest =
    {
        Page: PageRequest
        Filter: CaseListFilter<string>
    }
