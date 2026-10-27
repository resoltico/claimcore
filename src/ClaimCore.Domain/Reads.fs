namespace ClaimCore.Domain

open System

[<RequireQualifiedAccess>]
type HistoryDetail =
    | Summary
    | Full

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

type CaseGetRequest = { CaseReference: string }

type OperationLookup = { OperationId: Guid }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module HistoryDetail =
    let vocabulary =
        Vocabulary.ofUnion<HistoryDetail> "historyDetail" TokenCasing.ScreamingSnake None
