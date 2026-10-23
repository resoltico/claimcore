namespace ClaimCore.Contract

open System

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
