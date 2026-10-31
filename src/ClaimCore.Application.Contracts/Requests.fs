namespace ClaimCore.Application.Contracts

open System
open ClaimCore.Domain

type PageRequest =
    {
        AfterCursor: string option
        Limit: int
    }

type HistoryRequest =
    {
        CaseReference: string
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

type CaseGetRequest = { CaseReference: string }

type OperationLookup = { OperationId: Guid }


type CaseListRequest =
    {
        Page: PageRequest
        Filter: CaseListFilter<string>
    }
