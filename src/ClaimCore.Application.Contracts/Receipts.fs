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
        CaseId: CaseId
        Reference: CaseReference
        Revision: Revision
        Act: CaseActKind
        Snapshot: SnapshotDisclosure
    }

type AuthorityReceiptEffect =
    {
        Act: AuthorityKind
        Position: LedgerPosition
    }

[<RequireQualifiedAccess>]
type AuthorityTargetDisclosure =
    | Disclosed of ActorRef
    | Withheld of ActorId

module AuthorityTargetDisclosure =
    let actorId =
        function
        | AuthorityTargetDisclosure.Disclosed actor -> actor.ActorId
        | AuthorityTargetDisclosure.Withheld actor -> actor

type AuthorityReceipt =
    {
        Effects: AuthorityReceiptEffect list
        TargetActor: AuthorityTargetDisclosure
        Act: AuthorityKind
    }

/// Historical receipt bounds follow the retained operation format, not current authoring admission.
module AuthorityReceipt =
    [<Literal>]
    let MaximumEffects = 50

[<RequireQualifiedAccess>]
type WorkDisclosure =
    | Disclosed of CaseWorkReadModel
    | Withheld
    | Erased

type WorkReceipt =
    {
        Reference: CaseReference
        CaseId: CaseId
        Revision: Revision
        Act: WorkActKind
        Snapshot: WorkDisclosure
    }

[<RequireQualifiedAccess>]
type ReminderDisclosure =
    | Disclosed of Reminder
    | Withheld
    | Erased

type ReminderVersion =
    {
        ReminderId: ReminderId
        Revision: Revision
        Snapshot: ReminderDisclosure
    }

[<RequireQualifiedAccess>]
type ReminderReceiptEffects =
    | Single of ReminderVersion
    | CompletedAndPlanned of completed: ReminderVersion * next: ReminderVersion

type ReminderReceipt =
    {
        Reference: CaseReference
        CaseId: CaseId
        Act: ReminderActKind
        Effects: ReminderReceiptEffects
    }

[<RequireQualifiedAccess>]
type ReceiptSubject =
    | Case of CaseReceipt
    | Authority of AuthorityReceipt
    | Work of WorkReceipt
    | Reminder of ReminderReceipt

type OperationReceipt =
    {
        OperationId: OperationId
        Position: LedgerPosition
        Subject: ReceiptSubject
        RecordedAt: DateTimeOffset
        RecordedBy: ActorRef
        Basis: WorkDateBasis
    }
