namespace ClaimCore.Domain


open System

type LedgerPosition = { Sequence: int64; EntryHash: string }

[<RequireQualifiedAccess>]
type SnapshotDisclosure =
    | Disclosed of LiveCase
    | Withheld
    | Erased

type CaseReceipt =
    {
        Reference: CaseReference
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
type ReceiptSubject =
    | Case of CaseReceipt
    | Authority of AuthorityReceipt

type OperationReceipt =
    {
        OperationId: Guid
        Position: LedgerPosition
        Subject: ReceiptSubject
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
