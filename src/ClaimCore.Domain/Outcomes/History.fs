namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type HistoryEntry =
    | SummaryEntry of ChangeSummary
    | FullEntry of OperationReceipt

type HistoryResultPage =
    {
        Entries: HistoryEntry list
        NextCursor: string option
    }
