namespace ClaimCore.Contract

open System

type LedgerPosition = { Sequence: int64; EntryHash: string }

[<RequireQualifiedAccess>]
type SnapshotDisclosure =
    | Disclosed of CaseView
    | Withheld
    | Erased

type CaseReceipt =
    {
        Reference: string
        Revision: int64
        Act: CaseActKind
        Snapshot: SnapshotDisclosure
    }

type AuthorityReceipt =
    {
        TargetActorId: Guid
        Act: AuthorityKind
    }

[<RequireQualifiedAccess>]
type ReceiptScope =
    | Case of CaseReceipt
    | Authority of AuthorityReceipt

type OperationReceipt =
    {
        OperationId: Guid
        Position: LedgerPosition
        Scope: ReceiptScope
        RecordedAt: DateTimeOffset
        RecordedBy: Guid
    }

type ChangeSummary =
    {
        OperationId: Guid
        Position: LedgerPosition
        Revision: int64
        Act: CaseActKind
        RecordedAt: DateTimeOffset
        RecordedBy: Guid
    }
