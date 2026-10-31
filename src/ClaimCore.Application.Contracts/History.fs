namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

type LifecycleChange =
    {
        Before: Lifecycle option
        After: Lifecycle
    }

type HistoryDifference =
    {
        Lifecycle: LifecycleChange option
        Fields: FieldDiff list
    }

type FullHistoryEntry =
    {
        Receipt: OperationReceipt
        Difference: HistoryDifference option
    }

[<RequireQualifiedAccess>]
type HistoryEntry =
    | SummaryEntry of ChangeSummary
    | FullEntry of FullHistoryEntry

type HistoryResultPage =
    {
        CaseId: CaseId
        Reference: CaseReference
        Entries: HistoryEntry list
        NextCursor: string option
    }
