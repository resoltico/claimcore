namespace ClaimCore.Application.Contracts

open ClaimCore.Domain


open System


[<RequireQualifiedAccess>]
type SnapshotDisclosure =
    | Disclosed of LiveCase
    | Withheld
    | Erased

type CaseReceipt =
    {
        Reference: CaseReference
        Revision: Revision
        Act: CaseActKind
        Snapshot: SnapshotDisclosure
    }

type AuthorityReceipt =
    {
        TargetActorId: ActorId
        Act: AuthorityKind
    }

[<RequireQualifiedAccess>]
type ReceiptSubject =
    | Case of CaseReceipt
    | Authority of AuthorityReceipt

type OperationReceipt =
    {
        OperationId: OperationId
        Position: LedgerPosition
        Subject: ReceiptSubject
        RecordedAt: DateTimeOffset
        RecordedBy: ActorId
    }

type ChangeSummary =
    {
        OperationId: OperationId
        Position: LedgerPosition
        Revision: Revision
        Act: CaseActKind
        RecordedAt: DateTimeOffset
        RecordedBy: ActorId
    }
